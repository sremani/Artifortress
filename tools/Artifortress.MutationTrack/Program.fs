module Artifortress.MutationTrack.Program

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions

type FailureClass =
    | Success
    | NoMutantsGenerated
    | QuarantinedCompileErrors
    | BlockedFSharpNotSupported
    | BlockedToolMissing
    | InfraNugetUnavailable
    | InitialTestFailure
    | UnknownFailure

type MutationRunResult = {
    StartedAtUtc: DateTimeOffset
    FinishedAtUtc: DateTimeOffset
    ExitCode: int
    Classification: FailureClass
    Summary: string
    CandidateCount: int option
    MappedSpanCount: int option
    PlannedMutantCount: int option
    CreatedMutantCount: int option
    QuarantinedCompileErrorCount: int option
    LogPath: string
    ReportPath: string
}

type MutationOptions = {
    TestProject: string
    ProjectUnderTest: string
    MutateScope: string
    MutationLevel: string
    Concurrency: int
    OutputPath: string
    LogPath: string
    ReportPath: string
    ResultJsonPath: string
    RunBaselineTests: bool
    AllowBlocked: bool
    StrykerCliPath: string option
}

type FsharpOperatorFamily =
    | Boolean
    | Comparison
    | Arithmetic

type FsharpCompileValidationOptions = {
    ProjectPath: string
    SourceFilePath: string
    MaxMutants: int
    ScratchRoot: string
    ReportPath: string
    ResultJsonPath: string
}

type FsharpOperatorCandidate = {
    Line: int
    Column: int
    StartIndex: int
    OperatorToken: string
    ReplacementToken: string
    Family: FsharpOperatorFamily
}

type FsharpCompileValidationEntry = {
    Candidate: FsharpOperatorCandidate
    MutantPath: string
    ExitCode: int
    Succeeded: bool
    FirstErrorLine: string option
}

type FsharpCompileValidationResult = {
    StartedAtUtc: DateTimeOffset
    FinishedAtUtc: DateTimeOffset
    TotalCandidatesDiscovered: int
    SelectedMutantCount: int
    SuccessfulCompileCount: int
    FailedCompileCount: int
    Entries: FsharpCompileValidationEntry list
}

type FsharpNativeRuntimeOptions = {
    SourceFilePath: string
    TestProjectPath: string
    MaxMutants: int
    ScratchRoot: string
    ReportPath: string
    ResultJsonPath: string
}

type FsharpNativeMutantStatus =
    | Killed
    | Survived
    | CompileError
    | InfrastructureError

type FsharpNativeMutantEntry = {
    Index: int
    Candidate: FsharpOperatorCandidate
    MutantWorkspacePath: string
    Status: FsharpNativeMutantStatus
    ExitCode: int
    FirstErrorLine: string option
    DurationMs: int64
}

type FsharpNativeRuntimeResult = {
    StartedAtUtc: DateTimeOffset
    FinishedAtUtc: DateTimeOffset
    TotalCandidatesDiscovered: int
    SelectedMutantCount: int
    KilledCount: int
    SurvivedCount: int
    CompileErrorCount: int
    InfrastructureErrorCount: int
    Entries: FsharpNativeMutantEntry list
}

let private nowUtc () = DateTimeOffset.UtcNow
let private utf8NoBom = new UTF8Encoding(false)

let private quoteIfNeeded (value: string) =
    if String.IsNullOrWhiteSpace value then
        "\"\""
    elif value.Contains(" ") || value.Contains("\"") then
        "\"" + value.Replace("\"", "\\\"") + "\""
    else
        value

let private ensureParentDirectory (path: string) =
    let dir = Path.GetDirectoryName(path)

    if not (String.IsNullOrWhiteSpace dir) then
        Directory.CreateDirectory(dir) |> ignore

let private containsAny (haystack: string) (needles: string list) =
    needles
    |> List.exists (fun needle -> haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)

let private classifyFailure (exitCode: int) (logText: string) =
    if
        containsAny
            logText
            [ "No mutations found"
              "No mutants found"
              "No source files were selected for mutation"
              "0 mutants created"
              "0 mutants generated"
              "0 mutants were generated"
              "mutant-free world, nothing to test" ]
    then
        NoMutantsGenerated, "Mutation run completed but no mutants were generated."
    elif
        containsAny
            logText
            [ "mutants got status CompileError"
              "F# mutant is quarantined pending runtime activation support." ]
    then
        QuarantinedCompileErrors, "Mutants were generated and quarantined as compile errors pending runtime activation."
    elif exitCode = 0 then
        Success, "Stryker completed successfully."
    elif
        containsAny
            logText
            [ "Language not supported: Fsharp"
              "Mutation testing of F# projects is not ready yet. No mutants will be generated." ]
    then
        BlockedFSharpNotSupported, "Known blocker: Stryker does not yet support F# mutation in this execution path."
    elif
        containsAny
            logText
            [ "dotnet-stryker does not exist"
              "specified command or file was not found"
              "No executable found matching command \"dotnet-stryker\""
              "No such file or directory"
              "cannot find the file specified" ]
    then
        BlockedToolMissing, "dotnet-stryker tool is unavailable. Install/restore local dotnet tools first."
    elif
        containsAny
            logText
            [ "Unable to load the service index for source https://api.nuget.org/v3/index.json"
              "NU1301"
              "failed to retrieve information from remote source" ]
    then
        InfraNugetUnavailable, "NuGet/network dependency is unavailable."
    elif
        containsAny
            logText
            [ "One or more tests failed"
              "failed on initial test run"
              "break-on-initial-test-failure" ]
    then
        InitialTestFailure, "Initial tests failed before/while mutation execution."
    else
        UnknownFailure, "Unknown mutation failure. Inspect log output."

let private runProcess (logWriter: StreamWriter) (workingDir: string) (fileName: string) (args: string list) =
    try
        let info = ProcessStartInfo()
        info.FileName <- fileName
        info.WorkingDirectory <- workingDir
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        info.Arguments <- args |> List.map quoteIfNeeded |> String.concat " "

        use proc = new Process()
        proc.StartInfo <- info

        if not (proc.Start()) then
            failwithf "Failed to start process: %s %s" fileName info.Arguments

        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()

        let outText = stdout.Result
        let errText = stderr.Result
        let merged = outText + errText

        if not (String.IsNullOrWhiteSpace merged) then
            logWriter.WriteLine(merged)
            logWriter.Flush()
            Console.Write(merged)

        proc.ExitCode, merged
    with ex ->
        let message = $"process_start_error={ex.Message}"
        logWriter.WriteLine(message)
        logWriter.Flush()
        eprintfn "%s" message
        127, message

let private runProcessCapture (workingDir: string) (fileName: string) (args: string list) =
    try
        let info = ProcessStartInfo()
        info.FileName <- fileName
        info.WorkingDirectory <- workingDir
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        info.Arguments <- args |> List.map quoteIfNeeded |> String.concat " "

        use proc = new Process()
        proc.StartInfo <- info

        if not (proc.Start()) then
            failwithf "Failed to start process: %s %s" fileName info.Arguments

        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()
        let merged = stdout.Result + stderr.Result
        proc.ExitCode, merged
    with ex ->
        127, $"process_start_error={ex.Message}"

let rec private copyDirectoryRecursive (sourceDir: string) (targetDir: string) =
    Directory.CreateDirectory(targetDir) |> ignore

    for file in Directory.GetFiles(sourceDir) do
        let targetFile = Path.Combine(targetDir, Path.GetFileName(file))
        File.Copy(file, targetFile, true)

    for directory in Directory.GetDirectories(sourceDir) do
        let name = Path.GetFileName(directory)

        if
            not (
                name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".cache", StringComparison.OrdinalIgnoreCase)
                || name.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
                || name.Equals("chengis", StringComparison.OrdinalIgnoreCase)
            )
        then
            let targetSubDirectory = Path.Combine(targetDir, name)
            copyDirectoryRecursive directory targetSubDirectory

let private defaultOptionsForSpike () =
    { TestProject = "tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj"
      ProjectUnderTest = "src/Artifortress.Domain/Artifortress.Domain.fsproj"
      MutateScope = "src/Artifortress.Domain/Library.fs"
      MutationLevel = "Basic"
      Concurrency = 1
      OutputPath = "artifacts/mutation/mut01-domain"
      LogPath = "/tmp/artifortress-mutation-fsharp-spike.log"
      ReportPath = "docs/reports/mutation-spike-fsharp-latest.md"
      ResultJsonPath = "artifacts/mutation/mutation-spike-latest.json"
      RunBaselineTests = true
      AllowBlocked = true
      StrykerCliPath = None }

let private defaultOptionsForFsharpCompileValidation () =
    { ProjectPath = "src/Artifortress.Domain/Artifortress.Domain.fsproj"
      SourceFilePath = "src/Artifortress.Domain/Library.fs"
      MaxMutants = 9
      ScratchRoot = "artifacts/mutation/mut07c-compile"
      ReportPath = "docs/reports/mutation-trackb-mut07c-compile-validation.md"
      ResultJsonPath = "artifacts/mutation/mut07c-compile-validation.json" }

let private defaultOptionsForFsharpNativeRuntime () =
    { SourceFilePath = "src/Artifortress.Domain/Library.fs"
      TestProjectPath = "tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj"
      MaxMutants = 12
      ScratchRoot = "artifacts/mutation/native-fsharp-runtime"
      ReportPath = "docs/reports/mutation-native-fsharp-latest.md"
      ResultJsonPath = "artifacts/mutation/mutation-native-fsharp-latest.json" }

let private withOverridesFromArgs (options: MutationOptions) (arguments: Map<string, string>) =
    let tryGet key =
        match arguments.TryFind(key) with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    let parseBoolOrDefault defaultValue (raw: string) =
        match Boolean.TryParse(raw) with
        | true, parsed -> parsed
        | _ -> defaultValue

    let parseIntOrDefault defaultValue (raw: string) =
        match Int32.TryParse(raw) with
        | true, parsed when parsed > 0 -> parsed
        | _ -> defaultValue

    { options with
        TestProject = tryGet "test-project" |> Option.defaultValue options.TestProject
        ProjectUnderTest = tryGet "project" |> Option.defaultValue options.ProjectUnderTest
        MutateScope = tryGet "mutate" |> Option.defaultValue options.MutateScope
        MutationLevel = tryGet "mutation-level" |> Option.defaultValue options.MutationLevel
        Concurrency =
            tryGet "concurrency"
            |> Option.map (parseIntOrDefault options.Concurrency)
            |> Option.defaultValue options.Concurrency
        OutputPath = tryGet "output" |> Option.defaultValue options.OutputPath
        LogPath = tryGet "log" |> Option.defaultValue options.LogPath
        ReportPath = tryGet "report" |> Option.defaultValue options.ReportPath
        ResultJsonPath = tryGet "result-json" |> Option.defaultValue options.ResultJsonPath
        RunBaselineTests =
            tryGet "run-baseline-tests"
            |> Option.map (parseBoolOrDefault options.RunBaselineTests)
            |> Option.defaultValue options.RunBaselineTests
        AllowBlocked =
            tryGet "allow-blocked"
            |> Option.map (parseBoolOrDefault options.AllowBlocked)
            |> Option.defaultValue options.AllowBlocked
        StrykerCliPath = tryGet "stryker-cli" }

let private withFsharpCompileValidationOverrides (options: FsharpCompileValidationOptions) (arguments: Map<string, string>) =
    let tryGet key =
        match arguments.TryFind(key) with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    let parseIntOrDefault defaultValue (raw: string) =
        match Int32.TryParse(raw) with
        | true, parsed when parsed > 0 -> parsed
        | _ -> defaultValue

    { options with
        ProjectPath = tryGet "project" |> Option.defaultValue options.ProjectPath
        SourceFilePath = tryGet "source-file" |> Option.defaultValue options.SourceFilePath
        MaxMutants =
            tryGet "max-mutants"
            |> Option.map (parseIntOrDefault options.MaxMutants)
            |> Option.defaultValue options.MaxMutants
        ScratchRoot = tryGet "scratch-root" |> Option.defaultValue options.ScratchRoot
        ReportPath = tryGet "report" |> Option.defaultValue options.ReportPath
        ResultJsonPath = tryGet "result-json" |> Option.defaultValue options.ResultJsonPath }

let private withFsharpNativeRuntimeOverrides (options: FsharpNativeRuntimeOptions) (arguments: Map<string, string>) =
    let tryGet key =
        match arguments.TryFind(key) with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    let parseIntOrDefault defaultValue (raw: string) =
        match Int32.TryParse(raw) with
        | true, parsed when parsed > 0 -> parsed
        | _ -> defaultValue

    { options with
        SourceFilePath = tryGet "source-file" |> Option.defaultValue options.SourceFilePath
        TestProjectPath = tryGet "test-project" |> Option.defaultValue options.TestProjectPath
        MaxMutants =
            tryGet "max-mutants"
            |> Option.map (parseIntOrDefault options.MaxMutants)
            |> Option.defaultValue options.MaxMutants
        ScratchRoot = tryGet "scratch-root" |> Option.defaultValue options.ScratchRoot
        ReportPath = tryGet "report" |> Option.defaultValue options.ReportPath
        ResultJsonPath = tryGet "result-json" |> Option.defaultValue options.ResultJsonPath }

let private formatFailureClass (value: FailureClass) =
    match value with
    | Success -> "success"
    | NoMutantsGenerated -> "no_mutants_generated"
    | QuarantinedCompileErrors -> "quarantined_compile_errors"
    | BlockedFSharpNotSupported -> "blocked_fsharp_not_supported"
    | BlockedToolMissing -> "blocked_tool_missing"
    | InfraNugetUnavailable -> "infra_nuget_unavailable"
    | InitialTestFailure -> "initial_test_failure"
    | UnknownFailure -> "unknown_failure"

let private asNullableString (value: string option) =
    match value with
    | Some text when not (String.IsNullOrWhiteSpace text) -> text
    | _ -> "dotnet tool run dotnet-stryker"

let private jsonEscape (value: string) =
    if isNull value then
        ""
    else
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")

let private formatOptionalInt (value: int option) =
    match value with
    | Some number -> string number
    | None -> "null"

let private markdownOptionalInt (value: int option) =
    match value with
    | Some number -> $"`{number}`"
    | None -> "`n/a`"

let private parseMetrics (logText: string) =
    let parseLastGroup (pattern: string) (groupName: string) =
        let matches = Regex.Matches(logText, pattern, RegexOptions.Multiline)
        if matches.Count = 0 then
            None
        else
            let raw: string = matches.[matches.Count - 1].Groups.[groupName].Value
            match Int32.TryParse(raw) with
            | true, parsed -> Some parsed
            | _ -> None

    let candidateCount =
        parseLastGroup @"with (?<candidate>\d+) operator candidates, (?<mapped>\d+) mapped spans, and (?<planned>\d+) planned mutants" "candidate"
    let mappedSpanCount =
        parseLastGroup @"with (?<candidate>\d+) operator candidates, (?<mapped>\d+) mapped spans, and (?<planned>\d+) planned mutants" "mapped"
    let plannedMutantCount =
        parseLastGroup @"with (?<candidate>\d+) operator candidates, (?<mapped>\d+) mapped spans, and (?<planned>\d+) planned mutants" "planned"
    let createdMutantCount = parseLastGroup @"\] (?<created>\d+) mutants created" "created"
    let quarantinedCompileErrorCount = parseLastGroup @"\] (?<compileerrors>\d+)\s+mutants got status CompileError" "compileerrors"

    candidateCount, mappedSpanCount, plannedMutantCount, createdMutantCount, quarantinedCompileErrorCount

let private fsharpOperatorRegex = Regex(@"(<=|>=|<>|&&|\|\||=|<|>|\+|-|\*|/)", RegexOptions.Compiled)

let private formatFsharpOperatorFamily (family: FsharpOperatorFamily) =
    match family with
    | Boolean -> "boolean"
    | Comparison -> "comparison"
    | Arithmetic -> "arithmetic"

let private formatFsharpNativeMutantStatus (status: FsharpNativeMutantStatus) =
    match status with
    | Killed -> "killed"
    | Survived -> "survived"
    | CompileError -> "compile_error"
    | InfrastructureError -> "infrastructure_error"

let private tryResolveFsharpReplacement (operatorToken: string) =
    match operatorToken with
    | "&&" -> Some("||", Boolean)
    | "||" -> Some("&&", Boolean)
    | "=" -> Some("<>", Comparison)
    | "<>" -> Some("=", Comparison)
    | "<" -> Some(">=", Comparison)
    | "<=" -> Some(">", Comparison)
    | ">" -> Some("<=", Comparison)
    | ">=" -> Some("<", Comparison)
    | "+" -> Some("-", Arithmetic)
    | "-" -> Some("+", Arithmetic)
    | "*" -> Some("/", Arithmetic)
    | "/" -> Some("*", Arithmetic)
    | _ -> None

let private isCompileValidationTokenSupported (_operatorToken: string) = true

let private isIdentifierCharacter (ch: char) =
    Char.IsLetterOrDigit ch || ch = '_' || ch = '\''

let private isCompileValidationContextSafe (line: string) (matchStart: int) (operatorToken: string) =
    let previousChar =
        if matchStart > 0 then
            Some line.[matchStart - 1]
        else
            None

    let nextChar =
        let nextIndex = matchStart + operatorToken.Length
        if nextIndex < line.Length then Some line.[nextIndex] else None

    let prefix =
        if matchStart <= 0 then
            ""
        else
            line.Substring(0, matchStart)

    match operatorToken with
    | "=" ->
        let isFirstLetBindingEquals =
            prefix.IndexOf("let ", StringComparison.Ordinal) >= 0
            && prefix.IndexOf("=", StringComparison.Ordinal) < 0

        previousChar = Some ' '
        && nextChar = Some ' '
        && not isFirstLetBindingEquals
        && (line.Contains("||", StringComparison.Ordinal)
            || line.Contains("&&", StringComparison.Ordinal)
            || line.Contains(" if ", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("if ", StringComparison.Ordinal))
    | "-" -> nextChar <> Some '>' && previousChar <> Some '<'
    | "<" ->
        nextChar <> Some '-'
        && nextChar <> Some '|'
        &&
        not (
            (previousChar |> Option.exists isIdentifierCharacter)
            && (nextChar |> Option.exists isIdentifierCharacter)
        )
    | ">" ->
        previousChar <> Some '-'
        && previousChar <> Some '|'
        &&
        not (
            (previousChar |> Option.exists isIdentifierCharacter)
            && (nextChar |> Option.exists isIdentifierCharacter)
        )
        &&
        not (
            (previousChar |> Option.exists isIdentifierCharacter)
            && (nextChar = Some ')' || nextChar = Some ']' || nextChar = Some ',' || nextChar = Some ':')
        )
    | _ -> true

let private sanitizeLineForOperatorScan (line: string) (initialBlockCommentDepth: int) =
    let chars = line.ToCharArray()
    let mutable blockCommentDepth = initialBlockCommentDepth
    let mutable inString = false
    let mutable escaping = false
    let mutable index = 0

    while index < chars.Length do
        let current = chars.[index]
        let next = if index + 1 < chars.Length then chars.[index + 1] else '\000'

        if blockCommentDepth > 0 then
            chars.[index] <- ' '

            if current = '(' && next = '*' then
                chars.[index + 1] <- ' '
                blockCommentDepth <- blockCommentDepth + 1
                index <- index + 1
            elif current = '*' && next = ')' then
                chars.[index + 1] <- ' '
                blockCommentDepth <- blockCommentDepth - 1
                index <- index + 1
        elif not inString && current = '/' && next = '/' then
            for commentIndex in index .. chars.Length - 1 do
                chars.[commentIndex] <- ' '

            index <- chars.Length
        elif not inString && current = '(' && next = '*' then
            chars.[index] <- ' '
            chars.[index + 1] <- ' '
            blockCommentDepth <- blockCommentDepth + 1
            index <- index + 1
        elif current = '"' && not escaping then
            inString <- not inString
            chars.[index] <- ' '
        elif inString then
            escaping <- current = '\\' && not escaping
            chars.[index] <- ' '
        else
            escaping <- false

        index <- index + 1

    new string(chars), blockCommentDepth

let private computeLineStartOffsets (sourceText: string) =
    let offsets = ResizeArray<int>()
    offsets.Add(0)

    for index in 0 .. sourceText.Length - 1 do
        if sourceText.[index] = '\n' then
            offsets.Add(index + 1)

    offsets.ToArray()

let private discoverFsharpOperatorCandidates (sourceText: string) (sourceLines: string array) =
    let lineOffsets = computeLineStartOffsets sourceText
    let mutable blockCommentDepth = 0
    let candidates = ResizeArray<FsharpOperatorCandidate>()

    for lineIndex in 0 .. sourceLines.Length - 1 do
        let sanitizedLine, nextBlockDepth = sanitizeLineForOperatorScan sourceLines.[lineIndex] blockCommentDepth
        blockCommentDepth <- nextBlockDepth
        let matches = fsharpOperatorRegex.Matches(sanitizedLine)

        for matchIndex in 0 .. matches.Count - 1 do
            let matched = matches.[matchIndex]

            match tryResolveFsharpReplacement matched.Value with
            | None -> ()
            | Some(replacementToken, family) ->
                if
                    isCompileValidationTokenSupported matched.Value
                    && isCompileValidationContextSafe sourceLines.[lineIndex] matched.Index matched.Value
                then
                    let startOffset = lineOffsets.[lineIndex] + matched.Index
                    candidates.Add(
                        { Line = lineIndex + 1
                          Column = matched.Index + 1
                          StartIndex = startOffset
                          OperatorToken = matched.Value
                          ReplacementToken = replacementToken
                          Family = family }
                    )

    candidates |> Seq.toList

let private selectCompileValidationCandidates (maxMutants: int) (candidates: FsharpOperatorCandidate list) =
    let deduped = candidates |> List.distinctBy (fun candidate -> candidate.StartIndex)

    let seededByFamily =
        [ Boolean; Comparison; Arithmetic ]
        |> List.choose (fun family -> deduped |> List.tryFind (fun candidate -> candidate.Family = family))

    let seedStarts = seededByFamily |> List.map (fun candidate -> candidate.StartIndex) |> Set.ofList

    let remainder =
        deduped
        |> List.filter (fun candidate -> not (seedStarts.Contains candidate.StartIndex))

    (seededByFamily @ remainder) |> List.truncate maxMutants

let private tryFindFirstBuildErrorLine (buildOutput: string) =
    buildOutput.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.tryFind (fun line -> line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0)

let private pathEndsWithDirectorySeparator (path: string) =
    path.EndsWith(string Path.DirectorySeparatorChar, StringComparison.Ordinal)
    || path.EndsWith(string Path.AltDirectorySeparatorChar, StringComparison.Ordinal)

let private isPathWithinRoot (rootPath: string) (candidatePath: string) =
    let normalizedRoot =
        let fullRoot = Path.GetFullPath(rootPath)
        if pathEndsWithDirectorySeparator fullRoot then fullRoot else fullRoot + string Path.DirectorySeparatorChar

    let normalizedCandidate = Path.GetFullPath(candidatePath)
    normalizedCandidate.StartsWith(normalizedRoot, StringComparison.Ordinal)

let private isRelativePathWithinParent (relativePath: string) =
    not (String.IsNullOrWhiteSpace relativePath)
    && not (Path.IsPathRooted relativePath)
    && relativePath <> ".."
    && not (relativePath.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal))
    && not (relativePath.StartsWith(".." + string Path.AltDirectorySeparatorChar, StringComparison.Ordinal))

let private createRunScratchRoot (baseScratchRoot: string) =
    Directory.CreateDirectory(baseScratchRoot) |> ignore
    let runSuffix = Guid.NewGuid().ToString("N").Substring(0, 8)
    let runId = $"run-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{runSuffix}"
    let runScratchRoot = Path.Combine(baseScratchRoot, runId)
    Directory.CreateDirectory(runScratchRoot) |> ignore
    runScratchRoot

let private classifyNativeMutantRun (exitCode: int) (output: string) =
    if exitCode = 0 then
        Survived
    elif
        containsAny
            output
            [ ": error "
              "error FS"
              "error CS"
              "Build FAILED." ]
    then
        CompileError
    elif
        containsAny
            output
            [ "Failed!"
              "tests failed"
              "One or more tests failed" ]
    then
        Killed
    else
        InfrastructureError

let private writeFsharpCompileValidationJsonReport
    (options: FsharpCompileValidationOptions)
    (result: FsharpCompileValidationResult)
    =
    ensureParentDirectory options.ResultJsonPath

    use writer = new StreamWriter(options.ResultJsonPath, false, utf8NoBom)
    let generatedAtText = (nowUtc ()).ToString("yyyy-MM-ddTHH:mm:ssZ")
    let startedAtText = result.StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
    let finishedAtText = result.FinishedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")

    writer.WriteLine("{")
    writer.WriteLine($"  \"generatedAtUtc\": \"{generatedAtText}\",")
    writer.WriteLine($"  \"startedAtUtc\": \"{startedAtText}\",")
    writer.WriteLine($"  \"finishedAtUtc\": \"{finishedAtText}\",")
    writer.WriteLine($"  \"projectPath\": \"{jsonEscape options.ProjectPath}\",")
    writer.WriteLine($"  \"sourceFilePath\": \"{jsonEscape options.SourceFilePath}\",")
    writer.WriteLine($"  \"maxMutants\": {options.MaxMutants},")
    writer.WriteLine($"  \"totalCandidatesDiscovered\": {result.TotalCandidatesDiscovered},")
    writer.WriteLine($"  \"selectedMutantCount\": {result.SelectedMutantCount},")
    writer.WriteLine($"  \"successfulCompileCount\": {result.SuccessfulCompileCount},")
    writer.WriteLine($"  \"failedCompileCount\": {result.FailedCompileCount},")
    writer.WriteLine("  \"entries\": [")

    result.Entries
    |> List.iteri (fun index entry ->
        let delimiter =
            if index + 1 = result.Entries.Length then
                ""
            else
                ","

        let firstErrorText =
            match entry.FirstErrorLine with
            | Some value -> $"\"{jsonEscape value}\""
            | None -> "null"

        writer.WriteLine("    {")
        writer.WriteLine($"      \"line\": {entry.Candidate.Line},")
        writer.WriteLine($"      \"column\": {entry.Candidate.Column},")
        writer.WriteLine($"      \"family\": \"{formatFsharpOperatorFamily entry.Candidate.Family}\",")
        writer.WriteLine($"      \"operatorToken\": \"{jsonEscape entry.Candidate.OperatorToken}\",")
        writer.WriteLine($"      \"replacementToken\": \"{jsonEscape entry.Candidate.ReplacementToken}\",")
        writer.WriteLine($"      \"mutantPath\": \"{jsonEscape entry.MutantPath}\",")
        writer.WriteLine($"      \"exitCode\": {entry.ExitCode},")
        writer.WriteLine($"      \"succeeded\": {entry.Succeeded.ToString().ToLowerInvariant()},")
        writer.WriteLine($"      \"firstErrorLine\": {firstErrorText}")
        writer.WriteLine($"    }}{delimiter}")
    )

    writer.WriteLine("  ]")
    writer.WriteLine("}")

let private writeFsharpCompileValidationMarkdownReport
    (options: FsharpCompileValidationOptions)
    (result: FsharpCompileValidationResult)
    =
    ensureParentDirectory options.ReportPath

    use writer = new StreamWriter(options.ReportPath, false, utf8NoBom)
    let generatedAtText = (nowUtc ()).ToString("yyyy-MM-ddTHH:mm:ssZ")

    writer.WriteLine("# MUT-07c F# Compile Validation Report")
    writer.WriteLine()
    writer.WriteLine($"Generated at: {generatedAtText}")
    writer.WriteLine()
    writer.WriteLine("## Inputs")
    writer.WriteLine()
    writer.WriteLine($"- project path: `{options.ProjectPath}`")
    writer.WriteLine($"- source file path: `{options.SourceFilePath}`")
    writer.WriteLine($"- max mutants: `{options.MaxMutants}`")
    writer.WriteLine($"- scratch root: `{options.ScratchRoot}`")
    writer.WriteLine()
    writer.WriteLine("## Summary")
    writer.WriteLine()
    writer.WriteLine($"- discovered candidates: `{result.TotalCandidatesDiscovered}`")
    writer.WriteLine($"- selected mutants: `{result.SelectedMutantCount}`")
    writer.WriteLine($"- compile successes: `{result.SuccessfulCompileCount}`")
    writer.WriteLine($"- compile failures: `{result.FailedCompileCount}`")
    writer.WriteLine()
    writer.WriteLine("## Sample Results")
    writer.WriteLine()
    writer.WriteLine("| # | Location | Family | Mutation | Compile |")
    writer.WriteLine("|---|---|---|---|---|")

    result.Entries
    |> List.iteri (fun index entry ->
        let compileStatus =
            if entry.Succeeded then
                "pass"
            else
                "fail"

        writer.WriteLine(
            $"| {index + 1} | L{entry.Candidate.Line}:C{entry.Candidate.Column} | {formatFsharpOperatorFamily entry.Candidate.Family} | `{entry.Candidate.OperatorToken} -> {entry.Candidate.ReplacementToken}` | {compileStatus} |"
        )
    )

    let failures = result.Entries |> List.filter (fun entry -> not entry.Succeeded)
    if failures.IsEmpty then
        writer.WriteLine()
        writer.WriteLine("All sampled mutants compiled successfully.")
    else
        writer.WriteLine()
        writer.WriteLine("### Compile Failures")
        writer.WriteLine()

        failures
        |> List.iter (fun entry ->
            let errorLine = entry.FirstErrorLine |> Option.defaultValue "No compiler error line was captured."
            writer.WriteLine(
                $"- L{entry.Candidate.Line}:C{entry.Candidate.Column} `{entry.Candidate.OperatorToken} -> {entry.Candidate.ReplacementToken}`: `{errorLine}`"
            )
        )

let private runFsharpCompileValidation (arguments: Map<string, string>) =
    let options =
        defaultOptionsForFsharpCompileValidation ()
        |> fun value -> withFsharpCompileValidationOverrides value arguments

    let projectPath = Path.GetFullPath(options.ProjectPath)
    let sourcePath = Path.GetFullPath(options.SourceFilePath)
    let scratchRoot = Path.GetFullPath(options.ScratchRoot)
    let workspaceRoot = Path.GetFullPath(Environment.CurrentDirectory)
    let artifactsRoot = Path.Combine(workspaceRoot, "artifacts") |> Path.GetFullPath

    if not (File.Exists projectPath) then
        eprintfn "Project does not exist: %s" projectPath
        1
    elif not (File.Exists sourcePath) then
        eprintfn "Source file does not exist: %s" sourcePath
        1
    elif not (isPathWithinRoot artifactsRoot scratchRoot) then
        eprintfn "Scratch root must be within %s. Received: %s" artifactsRoot scratchRoot
        1
    else
        let projectDirectory = Path.GetDirectoryName(projectPath)
        let sourceRelativePath = Path.GetRelativePath(projectDirectory, sourcePath)
        if not (isRelativePathWithinParent sourceRelativePath) then
            eprintfn
                "Source file must reside under project directory. Project: %s, Source: %s"
                projectDirectory
                sourcePath
            1
        else
            let sourceText = File.ReadAllText(sourcePath)
            let sourceLines = File.ReadAllLines(sourcePath)
            let allCandidates = discoverFsharpOperatorCandidates sourceText sourceLines
            let selectedCandidates = selectCompileValidationCandidates options.MaxMutants allCandidates
            let startedAt = nowUtc ()
            let runScratchRoot = createRunScratchRoot scratchRoot

            let entries = ResizeArray<FsharpCompileValidationEntry>()

            selectedCandidates
            |> List.iteri (fun index candidate ->
                let mutantPath = Path.Combine(runScratchRoot, $"mutant-{index + 1:D3}")
                copyDirectoryRecursive projectDirectory mutantPath
                let mutantSourcePath = Path.Combine(mutantPath, sourceRelativePath)
                let mutable succeeded = false
                let mutable exitCode = 1
                let mutable firstErrorLine: string option = None

                if
                    candidate.StartIndex < 0
                    || candidate.StartIndex + candidate.OperatorToken.Length > sourceText.Length
                    || sourceText.Substring(candidate.StartIndex, candidate.OperatorToken.Length) <> candidate.OperatorToken
                then
                    firstErrorLine <- Some "candidate_span_mismatch"
                else
                    let mutantSourceText =
                        sourceText.Remove(candidate.StartIndex, candidate.OperatorToken.Length).Insert(candidate.StartIndex, candidate.ReplacementToken)

                    File.WriteAllText(mutantSourcePath, mutantSourceText, utf8NoBom)
                    let mutantProjectPath = Path.Combine(mutantPath, Path.GetFileName(projectPath))

                    let compileExitCode, compileOutput =
                        runProcessCapture
                            mutantPath
                            "dotnet"
                            [ "build"
                              mutantProjectPath
                              "--configuration"
                              "Debug"
                              "-v"
                              "minimal"
                              "--nologo" ]

                    exitCode <- compileExitCode
                    succeeded <- compileExitCode = 0

                    if not succeeded then
                        firstErrorLine <- tryFindFirstBuildErrorLine compileOutput

                entries.Add(
                    { Candidate = candidate
                      MutantPath = mutantPath
                      ExitCode = exitCode
                      Succeeded = succeeded
                      FirstErrorLine = firstErrorLine }
                )
            )

            let entriesList = entries |> Seq.toList
            let successCount = entriesList |> List.filter (fun entry -> entry.Succeeded) |> List.length
            let failureCount = entriesList.Length - successCount

            let result =
                { StartedAtUtc = startedAt
                  FinishedAtUtc = nowUtc ()
                  TotalCandidatesDiscovered = allCandidates.Length
                  SelectedMutantCount = entriesList.Length
                  SuccessfulCompileCount = successCount
                  FailedCompileCount = failureCount
                  Entries = entriesList }

            writeFsharpCompileValidationMarkdownReport options result
            writeFsharpCompileValidationJsonReport options result

            printfn "mutation_track_compile_validation_discovered=%d" result.TotalCandidatesDiscovered
            printfn "mutation_track_compile_validation_selected=%d" result.SelectedMutantCount
            printfn "mutation_track_compile_validation_success=%d" result.SuccessfulCompileCount
            printfn "mutation_track_compile_validation_failed=%d" result.FailedCompileCount
            printfn "mutation_track_compile_validation_report=%s" options.ReportPath
            printfn "mutation_track_compile_validation_result_json=%s" options.ResultJsonPath

            if result.SelectedMutantCount = 0 then
                1
            elif result.FailedCompileCount = 0 then
                0
            else
                1

let private tryFindFirstFailureLine (output: string) =
    output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.tryFind (fun line ->
        line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0
        || line.IndexOf("Failed ", StringComparison.OrdinalIgnoreCase) >= 0
        || line.IndexOf("Assertion", StringComparison.OrdinalIgnoreCase) >= 0)

let private writeFsharpNativeRuntimeJsonReport
    (options: FsharpNativeRuntimeOptions)
    (result: FsharpNativeRuntimeResult)
    =
    ensureParentDirectory options.ResultJsonPath

    use writer = new StreamWriter(options.ResultJsonPath, false, utf8NoBom)
    let generatedAtText = (nowUtc ()).ToString("yyyy-MM-ddTHH:mm:ssZ")
    let startedAtText = result.StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
    let finishedAtText = result.FinishedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")

    writer.WriteLine("{")
    writer.WriteLine($"  \"generatedAtUtc\": \"{generatedAtText}\",")
    writer.WriteLine($"  \"startedAtUtc\": \"{startedAtText}\",")
    writer.WriteLine($"  \"finishedAtUtc\": \"{finishedAtText}\",")
    writer.WriteLine($"  \"sourceFilePath\": \"{jsonEscape options.SourceFilePath}\",")
    writer.WriteLine($"  \"testProjectPath\": \"{jsonEscape options.TestProjectPath}\",")
    writer.WriteLine($"  \"maxMutants\": {options.MaxMutants},")
    writer.WriteLine($"  \"totalCandidatesDiscovered\": {result.TotalCandidatesDiscovered},")
    writer.WriteLine($"  \"selectedMutantCount\": {result.SelectedMutantCount},")
    writer.WriteLine($"  \"killedCount\": {result.KilledCount},")
    writer.WriteLine($"  \"survivedCount\": {result.SurvivedCount},")
    writer.WriteLine($"  \"compileErrorCount\": {result.CompileErrorCount},")
    writer.WriteLine($"  \"infrastructureErrorCount\": {result.InfrastructureErrorCount},")
    writer.WriteLine("  \"entries\": [")

    result.Entries
    |> List.iteri (fun index entry ->
        let delimiter =
            if index + 1 = result.Entries.Length then
                ""
            else
                ","

        let firstErrorText =
            match entry.FirstErrorLine with
            | Some value -> $"\"{jsonEscape value}\""
            | None -> "null"

        writer.WriteLine("    {")
        writer.WriteLine($"      \"index\": {entry.Index},")
        writer.WriteLine($"      \"line\": {entry.Candidate.Line},")
        writer.WriteLine($"      \"column\": {entry.Candidate.Column},")
        writer.WriteLine($"      \"family\": \"{formatFsharpOperatorFamily entry.Candidate.Family}\",")
        writer.WriteLine($"      \"operatorToken\": \"{jsonEscape entry.Candidate.OperatorToken}\",")
        writer.WriteLine($"      \"replacementToken\": \"{jsonEscape entry.Candidate.ReplacementToken}\",")
        writer.WriteLine($"      \"mutantWorkspacePath\": \"{jsonEscape entry.MutantWorkspacePath}\",")
        writer.WriteLine($"      \"status\": \"{formatFsharpNativeMutantStatus entry.Status}\",")
        writer.WriteLine($"      \"exitCode\": {entry.ExitCode},")
        writer.WriteLine($"      \"durationMs\": {entry.DurationMs},")
        writer.WriteLine($"      \"firstErrorLine\": {firstErrorText}")
        writer.WriteLine($"    }}{delimiter}")
    )

    writer.WriteLine("  ]")
    writer.WriteLine("}")

let private writeFsharpNativeRuntimeMarkdownReport
    (options: FsharpNativeRuntimeOptions)
    (result: FsharpNativeRuntimeResult)
    =
    ensureParentDirectory options.ReportPath

    use writer = new StreamWriter(options.ReportPath, false, utf8NoBom)
    let generatedAtText = (nowUtc ()).ToString("yyyy-MM-ddTHH:mm:ssZ")

    writer.WriteLine("# Native F# Mutation Runtime Report")
    writer.WriteLine()
    writer.WriteLine($"Generated at: {generatedAtText}")
    writer.WriteLine()
    writer.WriteLine("## Inputs")
    writer.WriteLine()
    writer.WriteLine($"- source file path: `{options.SourceFilePath}`")
    writer.WriteLine($"- test project path: `{options.TestProjectPath}`")
    writer.WriteLine($"- max mutants: `{options.MaxMutants}`")
    writer.WriteLine($"- scratch root: `{options.ScratchRoot}`")
    writer.WriteLine()
    writer.WriteLine("## Summary")
    writer.WriteLine()
    writer.WriteLine($"- discovered candidates: `{result.TotalCandidatesDiscovered}`")
    writer.WriteLine($"- selected mutants: `{result.SelectedMutantCount}`")
    writer.WriteLine($"- killed: `{result.KilledCount}`")
    writer.WriteLine($"- survived: `{result.SurvivedCount}`")
    writer.WriteLine($"- compile errors: `{result.CompileErrorCount}`")
    writer.WriteLine($"- infrastructure errors: `{result.InfrastructureErrorCount}`")
    writer.WriteLine()
    writer.WriteLine("## Mutant Outcomes")
    writer.WriteLine()
    writer.WriteLine("| # | Location | Mutation | Status | Duration (ms) |")
    writer.WriteLine("|---|---|---|---|---|")

    result.Entries
    |> List.iter (fun entry ->
        writer.WriteLine(
            $"| {entry.Index} | L{entry.Candidate.Line}:C{entry.Candidate.Column} | `{entry.Candidate.OperatorToken} -> {entry.Candidate.ReplacementToken}` | {formatFsharpNativeMutantStatus entry.Status} | {entry.DurationMs} |"
        )
    )

let private runFsharpNativeRuntime (arguments: Map<string, string>) =
    let options =
        defaultOptionsForFsharpNativeRuntime ()
        |> fun value -> withFsharpNativeRuntimeOverrides value arguments

    let workspaceRoot = Path.GetFullPath(Environment.CurrentDirectory)
    let sourcePath = Path.GetFullPath(options.SourceFilePath)
    let testProjectPath = Path.GetFullPath(options.TestProjectPath)
    let scratchRoot = Path.GetFullPath(options.ScratchRoot)
    let artifactsRoot = Path.Combine(workspaceRoot, "artifacts") |> Path.GetFullPath

    if not (File.Exists(sourcePath)) then
        eprintfn "Source file does not exist: %s" sourcePath
        1
    elif not (File.Exists(testProjectPath)) then
        eprintfn "Test project does not exist: %s" testProjectPath
        1
    elif not (isPathWithinRoot artifactsRoot scratchRoot) then
        eprintfn "Scratch root must be within %s. Received: %s" artifactsRoot scratchRoot
        1
    else
        let sourceRelativePath = Path.GetRelativePath(workspaceRoot, sourcePath)
        let testProjectRelativePath = Path.GetRelativePath(workspaceRoot, testProjectPath)

        if not (isRelativePathWithinParent sourceRelativePath) then
            eprintfn "Source file must reside under workspace root. Source: %s" sourcePath
            1
        elif not (isRelativePathWithinParent testProjectRelativePath) then
            eprintfn "Test project must reside under workspace root. Test project: %s" testProjectPath
            1
        else
            let baselineExitCode, baselineOutput =
                runProcessCapture
                    workspaceRoot
                    "dotnet"
                    [ "test"
                      testProjectPath
                      "--configuration"
                      "Debug"
                      "-v"
                      "minimal"
                      "--nologo" ]

            if baselineExitCode <> 0 then
                eprintfn "Baseline tests failed. Aborting native mutation runtime."
                eprintfn "%s" baselineOutput
                1
            else
                let sourceText = File.ReadAllText(sourcePath)
                let sourceLines = File.ReadAllLines(sourcePath)
                let allCandidates = discoverFsharpOperatorCandidates sourceText sourceLines
                let selectedCandidates = selectCompileValidationCandidates options.MaxMutants allCandidates
                let startedAt = nowUtc ()
                let runScratchRoot = createRunScratchRoot scratchRoot

                let entries = ResizeArray<FsharpNativeMutantEntry>()

                selectedCandidates
                |> List.iteri (fun index candidate ->
                    let mutantIndex = index + 1
                    let mutantWorkspacePath = Path.Combine(runScratchRoot, $"mutant-{mutantIndex:D3}")
                    copyDirectoryRecursive workspaceRoot mutantWorkspacePath
                    let mutantSourcePath = Path.Combine(mutantWorkspacePath, sourceRelativePath)
                    let mutantTestProjectPath = Path.Combine(mutantWorkspacePath, testProjectRelativePath)
                    let mutable status = InfrastructureError
                    let mutable exitCode = 1
                    let mutable firstErrorLine: string option = None
                    let stopwatch = Stopwatch.StartNew()

                    if
                        candidate.StartIndex < 0
                        || candidate.StartIndex + candidate.OperatorToken.Length > sourceText.Length
                        || sourceText.Substring(candidate.StartIndex, candidate.OperatorToken.Length) <> candidate.OperatorToken
                    then
                        firstErrorLine <- Some "candidate_span_mismatch"
                    else
                        let mutantSourceText =
                            sourceText.Remove(candidate.StartIndex, candidate.OperatorToken.Length).Insert(candidate.StartIndex, candidate.ReplacementToken)

                        File.WriteAllText(mutantSourcePath, mutantSourceText, utf8NoBom)

                        let testExitCode, testOutput =
                            runProcessCapture
                                mutantWorkspacePath
                                "dotnet"
                                [ "test"
                                  mutantTestProjectPath
                                  "--configuration"
                                  "Debug"
                                  "-v"
                                  "minimal"
                                  "--nologo" ]

                        exitCode <- testExitCode
                        status <- classifyNativeMutantRun testExitCode testOutput

                        if status <> Survived then
                            firstErrorLine <- tryFindFirstBuildErrorLine testOutput |> Option.orElseWith (fun () -> tryFindFirstFailureLine testOutput)

                    stopwatch.Stop()

                    entries.Add(
                        { Index = mutantIndex
                          Candidate = candidate
                          MutantWorkspacePath = mutantWorkspacePath
                          Status = status
                          ExitCode = exitCode
                          FirstErrorLine = firstErrorLine
                          DurationMs = stopwatch.ElapsedMilliseconds }
                    )
                )

                let entriesList = entries |> Seq.toList
                let killedCount = entriesList |> List.filter (fun entry -> entry.Status = Killed) |> List.length
                let survivedCount = entriesList |> List.filter (fun entry -> entry.Status = Survived) |> List.length
                let compileErrorCount = entriesList |> List.filter (fun entry -> entry.Status = CompileError) |> List.length
                let infraCount = entriesList |> List.filter (fun entry -> entry.Status = InfrastructureError) |> List.length

                let result =
                    { StartedAtUtc = startedAt
                      FinishedAtUtc = nowUtc ()
                      TotalCandidatesDiscovered = allCandidates.Length
                      SelectedMutantCount = entriesList.Length
                      KilledCount = killedCount
                      SurvivedCount = survivedCount
                      CompileErrorCount = compileErrorCount
                      InfrastructureErrorCount = infraCount
                      Entries = entriesList }

                writeFsharpNativeRuntimeMarkdownReport options result
                writeFsharpNativeRuntimeJsonReport options result

                printfn "native_fsharp_mutation_discovered=%d" result.TotalCandidatesDiscovered
                printfn "native_fsharp_mutation_selected=%d" result.SelectedMutantCount
                printfn "native_fsharp_mutation_killed=%d" result.KilledCount
                printfn "native_fsharp_mutation_survived=%d" result.SurvivedCount
                printfn "native_fsharp_mutation_compile_errors=%d" result.CompileErrorCount
                printfn "native_fsharp_mutation_infra_errors=%d" result.InfrastructureErrorCount
                printfn "native_fsharp_mutation_report=%s" options.ReportPath
                printfn "native_fsharp_mutation_result_json=%s" options.ResultJsonPath

                if result.SelectedMutantCount = 0 then
                    1
                elif result.CompileErrorCount > 0 || result.InfrastructureErrorCount > 0 then
                    1
                else
                    0

let private writeJsonReport (result: MutationRunResult) (options: MutationOptions) =
    ensureParentDirectory options.ResultJsonPath

    use writer = new StreamWriter(options.ResultJsonPath, false, utf8NoBom)
    let generatedAtText = (nowUtc ()).ToString("yyyy-MM-ddTHH:mm:ssZ")
    let startedAtText = result.StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
    let finishedAtText = result.FinishedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
    writer.WriteLine("{")
    writer.WriteLine($"  \"generatedAtUtc\": \"{generatedAtText}\",")
    writer.WriteLine($"  \"startedAtUtc\": \"{startedAtText}\",")
    writer.WriteLine($"  \"finishedAtUtc\": \"{finishedAtText}\",")
    writer.WriteLine($"  \"classification\": \"{formatFailureClass result.Classification}\",")
    writer.WriteLine($"  \"exitCode\": {result.ExitCode},")
    writer.WriteLine($"  \"summary\": \"{jsonEscape result.Summary}\",")
    writer.WriteLine($"  \"candidateCount\": {formatOptionalInt result.CandidateCount},")
    writer.WriteLine($"  \"mappedSpanCount\": {formatOptionalInt result.MappedSpanCount},")
    writer.WriteLine($"  \"plannedMutantCount\": {formatOptionalInt result.PlannedMutantCount},")
    writer.WriteLine($"  \"createdMutantCount\": {formatOptionalInt result.CreatedMutantCount},")
    writer.WriteLine($"  \"quarantinedCompileErrorCount\": {formatOptionalInt result.QuarantinedCompileErrorCount},")
    writer.WriteLine($"  \"testProject\": \"{jsonEscape options.TestProject}\",")
    writer.WriteLine($"  \"projectUnderTest\": \"{jsonEscape options.ProjectUnderTest}\",")
    writer.WriteLine($"  \"mutateScope\": \"{jsonEscape options.MutateScope}\",")
    writer.WriteLine($"  \"outputPath\": \"{jsonEscape options.OutputPath}\",")
    writer.WriteLine($"  \"logPath\": \"{jsonEscape result.LogPath}\",")
    writer.WriteLine($"  \"reportPath\": \"{jsonEscape result.ReportPath}\",")
    writer.WriteLine($"  \"strykerCli\": \"{jsonEscape (asNullableString options.StrykerCliPath)}\"")
    writer.WriteLine("}")

let private writeMarkdownReport (result: MutationRunResult) (options: MutationOptions) =
    ensureParentDirectory result.ReportPath

    use writer = new StreamWriter(result.ReportPath, false, utf8NoBom)
    let generatedAtUtc = nowUtc ()
    let generatedAtText = generatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
    let startedAtText = result.StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
    let finishedAtText = result.FinishedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
    writer.WriteLine("# Mutation Track Report")
    writer.WriteLine()
    writer.WriteLine($"Generated at: {generatedAtText}")
    writer.WriteLine()
    writer.WriteLine("## Inputs")
    writer.WriteLine()
    writer.WriteLine($"- test project: `{options.TestProject}`")
    writer.WriteLine($"- project under mutation: `{options.ProjectUnderTest}`")
    writer.WriteLine($"- mutate scope: `{options.MutateScope}`")
    writer.WriteLine($"- mutation level: `{options.MutationLevel}`")
    writer.WriteLine($"- concurrency: `{options.Concurrency}`")
    writer.WriteLine($"- baseline tests executed: `{options.RunBaselineTests}`")
    writer.WriteLine($"- stryker cli: `{asNullableString options.StrykerCliPath}`")
    writer.WriteLine()
    writer.WriteLine("## Outcome")
    writer.WriteLine()
    writer.WriteLine($"- status: `{formatFailureClass result.Classification}`")
    writer.WriteLine($"- exit code: `{result.ExitCode}`")
    writer.WriteLine($"- summary: {result.Summary}")
    writer.WriteLine($"- started at: `{startedAtText}`")
    writer.WriteLine($"- finished at: `{finishedAtText}`")
    writer.WriteLine($"- log: `{result.LogPath}`")
    writer.WriteLine($"- output path: `{options.OutputPath}`")
    writer.WriteLine()
    writer.WriteLine("## Extracted Metrics")
    writer.WriteLine()
    writer.WriteLine($"- candidates discovered: {markdownOptionalInt result.CandidateCount}")
    writer.WriteLine($"- mapped spans: {markdownOptionalInt result.MappedSpanCount}")
    writer.WriteLine($"- planned mutants: {markdownOptionalInt result.PlannedMutantCount}")
    writer.WriteLine($"- created mutants: {markdownOptionalInt result.CreatedMutantCount}")
    writer.WriteLine($"- quarantined compile errors: {markdownOptionalInt result.QuarantinedCompileErrorCount}")
    writer.WriteLine()
    writer.WriteLine("## Next Action")
    writer.WriteLine()

    match result.Classification with
    | Success ->
        writer.WriteLine("- Expand mutate scope and start baseline scoring.")
    | NoMutantsGenerated ->
        writer.WriteLine("- Continue MUT-07/MUT-08: add F# mutators and rewrite safety so mutants can be generated.")
    | QuarantinedCompileErrors ->
        writer.WriteLine("- Continue post-plan runtime activation work: replace compile-error quarantine with executable tested mutant activation.")
    | BlockedFSharpNotSupported ->
        writer.WriteLine("- Continue Track B engine work (analyzer/mutator implementation in fork/upstream).")
    | BlockedToolMissing ->
        writer.WriteLine("- Restore local tools (`dotnet tool restore`) and rerun.")
    | InfraNugetUnavailable ->
        writer.WriteLine("- Retry when NuGet/network access is restored.")
    | InitialTestFailure ->
        writer.WriteLine("- Fix initial failing tests before mutation runs.")
    | UnknownFailure ->
        writer.WriteLine("- Inspect full log and classify the failure pattern.")

let private resolveStrykerCommand (options: MutationOptions) =
    let mutationArguments =
        [ "-tp"
          options.TestProject
          "-p"
          options.ProjectUnderTest
          "-m"
          options.MutateScope
          "-l"
          options.MutationLevel
          "-c"
          options.Concurrency.ToString()
          "-r"
          "ClearText"
          "-r"
          "Json"
          "-O"
          options.OutputPath
          "--break-at"
          "0"
          "--threshold-low"
          "0"
          "--threshold-high"
          "100"
          "--skip-version-check"
          "--disable-bail"
          "--break-on-initial-test-failure" ]

    match options.StrykerCliPath with
    | Some cliPath when not (String.IsNullOrWhiteSpace cliPath) ->
        if cliPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
            "dotnet", cliPath :: mutationArguments
        else
            cliPath, mutationArguments
    | _ ->
        "dotnet", [ "tool"; "run"; "dotnet-stryker"; "--" ] @ mutationArguments

let private runMutationFlow (options: MutationOptions) =
    ensureParentDirectory options.LogPath
    Directory.CreateDirectory(options.OutputPath) |> ignore

    let startedAt = nowUtc ()
    let startedAtIso = startedAt.ToString("O")

    use logWriter = new StreamWriter(options.LogPath, false, utf8NoBom)
    logWriter.WriteLine($"mutation_track_started_utc={startedAtIso}")
    logWriter.WriteLine($"test_project={options.TestProject}")
    logWriter.WriteLine($"project={options.ProjectUnderTest}")
    logWriter.WriteLine($"mutate={options.MutateScope}")
    logWriter.WriteLine($"stryker_cli={asNullableString options.StrykerCliPath}")
    logWriter.WriteLine()

    let baselineExitCode =
        if options.RunBaselineTests then
            logWriter.WriteLine("=== baseline test run ===")

            let exitCode, _ =
                runProcess
                    logWriter
                    Environment.CurrentDirectory
                    "dotnet"
                    [ "test"
                      options.TestProject
                      "--configuration"
                      "Debug"
                      "-v"
                      "minimal" ]

            exitCode
        else
            0

    let finalExitCode, finalText =
        if baselineExitCode <> 0 then
            baselineExitCode, "Baseline tests failed before mutation run."
        else
            logWriter.WriteLine()
            logWriter.WriteLine("=== stryker mutation run ===")
            let strykerCommand, strykerArgs = resolveStrykerCommand options

            runProcess
                logWriter
                Environment.CurrentDirectory
                strykerCommand
                strykerArgs

    let completeLogText =
        if File.Exists(options.LogPath) then
            File.ReadAllText(options.LogPath)
        else
            finalText

    let classification, summary = classifyFailure finalExitCode completeLogText
    let candidateCount, mappedSpanCount, plannedMutantCount, createdMutantCount, quarantinedCompileErrorCount = parseMetrics completeLogText

    let result =
        { StartedAtUtc = startedAt
          FinishedAtUtc = nowUtc ()
          ExitCode = finalExitCode
          Classification = classification
          Summary = summary
          CandidateCount = candidateCount
          MappedSpanCount = mappedSpanCount
          PlannedMutantCount = plannedMutantCount
          CreatedMutantCount = createdMutantCount
          QuarantinedCompileErrorCount = quarantinedCompileErrorCount
          LogPath = options.LogPath
          ReportPath = options.ReportPath }

    writeMarkdownReport result options
    writeJsonReport result options

    printfn "mutation_track_status=%s" (formatFailureClass classification)
    printfn "mutation_track_exit_code=%d" finalExitCode
    printfn "mutation_track_report=%s" options.ReportPath
    printfn "mutation_track_result_json=%s" options.ResultJsonPath

    match classification with
    | Success -> 0
    | NoMutantsGenerated when options.AllowBlocked -> 0
    | QuarantinedCompileErrors when options.AllowBlocked -> 0
    | BlockedFSharpNotSupported
    | BlockedToolMissing
    | InfraNugetUnavailable when options.AllowBlocked -> 0
    | _ -> 1

let private writeUsage () =
    printfn "Artifortress.MutationTrack"
    printfn ""
    printfn "Commands:"
    printfn "  spike                 Run default F# feasibility spike workflow."
    printfn "  run                   Run configurable mutation workflow."
    printfn "  validate-fsharp-mutants  Compile-validate sampled F# operator mutants."
    printfn "  run-fsharp-native     Run native F# mutation runtime against local tests."
    printfn "  report                Regenerate markdown report from existing log."
    printfn "  classify-failure      Classify a log and exit code."
    printfn ""
    printfn "Common options:"
    printfn "  --test-project <path>"
    printfn "  --project <path>"
    printfn "  --mutate <glob/span>"
    printfn "  --mutation-level <Basic|Standard|Advanced|Complete>"
    printfn "  --concurrency <int>"
    printfn "  --output <path>"
    printfn "  --log <path>"
    printfn "  --report <path>"
    printfn "  --result-json <path>"
    printfn "  --stryker-cli <path-to-stryker-cli-dll-or-executable>"
    printfn "  --run-baseline-tests <true|false>"
    printfn "  --allow-blocked <true|false>"
    printfn ""
    printfn "Compile-validation options:"
    printfn "  --source-file <path>"
    printfn "  --max-mutants <int>"
    printfn "  --scratch-root <path>"

let private parseArguments (args: string array) =
    let rec loop index (positionals: string list) (named: Map<string, string>) =
        if index >= args.Length then
            List.rev positionals, named
        else
            let current = args.[index]

            if current.StartsWith("--", StringComparison.Ordinal) then
                let key = current.Substring(2)

                if index + 1 < args.Length && not (args.[index + 1].StartsWith("--", StringComparison.Ordinal)) then
                    loop (index + 2) positionals (named.Add(key, args.[index + 1]))
                else
                    loop (index + 1) positionals (named.Add(key, "true"))
            else
                loop (index + 1) (current :: positionals) named

    loop 0 [] Map.empty

let private runReportFromExistingLog (arguments: Map<string, string>) =
    let options = defaultOptionsForSpike () |> fun value -> withOverridesFromArgs value arguments

    if not (File.Exists(options.LogPath)) then
        eprintfn "Log file does not exist: %s" options.LogPath
        1
    else
        let text = File.ReadAllText(options.LogPath)
        let exitCode =
            match arguments.TryFind("exit-code") with
            | Some raw ->
                match Int32.TryParse(raw) with
                | true, parsed -> parsed
                | _ -> 1
            | None -> 1

        let classification, summary = classifyFailure exitCode text
        let candidateCount, mappedSpanCount, plannedMutantCount, createdMutantCount, quarantinedCompileErrorCount = parseMetrics text

        let result =
            { StartedAtUtc = nowUtc ()
              FinishedAtUtc = nowUtc ()
              ExitCode = exitCode
              Classification = classification
              Summary = summary
              CandidateCount = candidateCount
              MappedSpanCount = mappedSpanCount
              PlannedMutantCount = plannedMutantCount
              CreatedMutantCount = createdMutantCount
              QuarantinedCompileErrorCount = quarantinedCompileErrorCount
              LogPath = options.LogPath
              ReportPath = options.ReportPath }

        writeMarkdownReport result options
        writeJsonReport result options
        printfn "report=%s" options.ReportPath
        printfn "result_json=%s" options.ResultJsonPath
        printfn "classification=%s" (formatFailureClass classification)
        0

let private runClassifyFailure (arguments: Map<string, string>) =
    let logPath =
        arguments.TryFind("log")
        |> Option.defaultValue "/tmp/artifortress-mutation-fsharp-spike.log"

    if not (File.Exists(logPath)) then
        eprintfn "Log file does not exist: %s" logPath
        1
    else
        let exitCode =
            match arguments.TryFind("exit-code") with
            | Some raw ->
                match Int32.TryParse(raw) with
                | true, parsed -> parsed
                | _ -> 1
            | None -> 1

        let text = File.ReadAllText(logPath)
        let classification, summary = classifyFailure exitCode text
        printfn """{"classification":"%s","summary":"%s","exitCode":%d,"logPath":"%s"}""" (formatFailureClass classification) (summary.Replace("\"", "\\\"")) exitCode logPath
        0

[<EntryPoint>]
let main argv =
    let positionals, arguments = parseArguments argv

    match positionals with
    | [] ->
        writeUsage ()
        1
    | command :: _ ->
        match command.Trim().ToLowerInvariant() with
        | "spike" ->
            let options = defaultOptionsForSpike () |> fun value -> withOverridesFromArgs value arguments
            runMutationFlow options
        | "run" ->
            let options =
                { defaultOptionsForSpike () with
                    ReportPath = "docs/reports/mutation-track-latest.md"
                    LogPath = "/tmp/artifortress-mutation-track.log"
                    ResultJsonPath = "artifacts/mutation/mutation-track-latest.json" }
                |> fun value -> withOverridesFromArgs value arguments

            runMutationFlow options
        | "report" -> runReportFromExistingLog arguments
        | "classify-failure" -> runClassifyFailure arguments
        | "validate-fsharp-mutants" -> runFsharpCompileValidation arguments
        | "run-fsharp-native" -> runFsharpNativeRuntime arguments
        | "help"
        | "--help"
        | "-h" ->
            writeUsage ()
            0
        | unknown ->
            eprintfn "Unknown command: %s" unknown
            writeUsage ()
            1
