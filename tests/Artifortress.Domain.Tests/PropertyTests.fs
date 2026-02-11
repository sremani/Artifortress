module PropertyTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open Artifortress.Domain
open Artifortress.Worker
open FsCheck
open FsCheck.Xunit
open Microsoft.Extensions.Configuration

let private normalizeExpectedServiceName (raw: string) =
    if String.IsNullOrWhiteSpace raw then
        "artifortress"
    else
        raw.Trim().ToLowerInvariant()

let private isValidRepoKeyCandidate (raw: string) =
    not (String.IsNullOrWhiteSpace raw) && not (raw.Contains ":")

let private normalizeNonNegativeInt (value: int) =
    if value = Int32.MinValue then 0 else abs value

let private normalizeNonNegativeInt64 (value: int64) =
    if value = Int64.MinValue then 0L else abs value

let private sanitizeRepoKey (raw: string) =
    let normalized =
        if String.IsNullOrWhiteSpace raw then
            "repo"
        else
            raw.Trim().ToLowerInvariant().Replace(":", "-")

    if String.IsNullOrWhiteSpace normalized then "repo" else normalized

let private createRepoScope (repoKey: string) (role: RepoRole) =
    match RepoScope.tryCreate repoKey role with
    | Ok scope -> scope
    | Error err -> failwithf "Unexpected repo scope creation error: %s" err

let private isLowerHexCharacter (ch: char) =
    (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')

let private sanitizeUriPathSegment (raw: string) =
    let candidate =
        if String.IsNullOrWhiteSpace raw then
            "packages-index"
        else
            raw.Trim().ToLowerInvariant()
            |> Seq.map (fun ch ->
                if Char.IsLetterOrDigit ch || ch = '-' || ch = '_' then
                    ch
                else
                    '-')
            |> Seq.toArray
            |> String

    if String.IsNullOrWhiteSpace candidate then "packages-index" else candidate

[<Property>]
let ``RepoRole value and parser roundtrip`` (role: RepoRole) =
    match RepoRole.tryParse (RepoRole.value role) with
    | Ok parsed -> parsed = role
    | Error _ -> false

[<Property>]
let ``RepoRole parser ignores casing and outer whitespace`` (role: RepoRole) (leading: int) (trailing: int) =
    let leftPad = String.replicate (abs leading % 3) " "
    let rightPad = String.replicate (abs trailing % 3) " "
    let text = $"{leftPad}{RepoRole.value role |> fun value -> value.ToUpperInvariant()}{rightPad}"

    match RepoRole.tryParse text with
    | Ok parsed -> parsed = role
    | Error _ -> false

[<Property>]
let ``ServiceName creation normalizes trim and lowercase`` (raw: string) =
    let input =
        if String.IsNullOrWhiteSpace raw then
            " ARTIFORTRESS "
        else
            raw

    match ServiceName.tryCreate input with
    | Ok serviceName -> ServiceName.value serviceName = normalizeExpectedServiceName input
    | Error _ -> false

[<Property>]
let ``RepoScope create-value-parse roundtrip for valid repo keys`` (repoKeyRaw: string) (role: RepoRole) =
    if not (isValidRepoKeyCandidate repoKeyRaw) then
        true
    else
        let repoKey = repoKeyRaw.Trim()

        match RepoScope.tryCreate repoKey role with
        | Error _ -> false
        | Ok scope ->
            let serialized = RepoScope.value scope

            match RepoScope.tryParse serialized with
            | Error _ -> false
            | Ok reparsed ->
                RepoScope.value reparsed = serialized
                && RepoScope.repoKey reparsed = RepoScope.repoKey scope
                && RepoScope.role reparsed = RepoScope.role scope

[<Property>]
let ``RepoScope create rejects keys containing colon delimiter`` (leftRaw: string) (rightRaw: string) (role: RepoRole) =
    let left = if String.IsNullOrWhiteSpace leftRaw then "left" else leftRaw.Trim()
    let right = if String.IsNullOrWhiteSpace rightRaw then "right" else rightRaw.Trim()
    let repoKey = $"{left}:{right}"

    match RepoScope.tryCreate repoKey role with
    | Ok _ -> false
    | Error err -> err = "Repository key cannot contain ':'."

[<Property>]
let ``Wildcard admin scope allows all roles for valid repo key`` (repoKeyRaw: string) (requiredRole: RepoRole) =
    if not (isValidRepoKeyCandidate repoKeyRaw) then
        true
    else
        let repoKey = repoKeyRaw.Trim()

        match RepoScope.tryCreate "*" RepoRole.Admin with
        | Error _ -> false
        | Ok adminScope -> RepoScope.allows repoKey requiredRole adminScope

[<Property>]
let ``RepoScope allows uses case-insensitive repo comparison with role implication`` (repoKeyRaw: string) (assignedRole: RepoRole) (requiredRole: RepoRole) =
    if not (isValidRepoKeyCandidate repoKeyRaw) then
        true
    else
        let repoKey = repoKeyRaw.Trim()
        let requiredRepo = $"  {repoKey.ToUpperInvariant()}  "

        match RepoScope.tryCreate repoKey assignedRole with
        | Error _ -> false
        | Ok scope ->
            let allowed = RepoScope.allows requiredRepo requiredRole scope
            allowed = RepoRole.implies assignedRole requiredRole

[<Property>]
let ``RepoRole implication is reflexive`` (role: RepoRole) = RepoRole.implies role role

[<Property>]
let ``RepoRole implication is transitive`` (left: RepoRole) (middle: RepoRole) (right: RepoRole) =
    let leftToMiddle = RepoRole.implies left middle
    let middleToRight = RepoRole.implies middle right
    let leftToRight = RepoRole.implies left right
    not (leftToMiddle && middleToRight) || leftToRight

[<Property>]
let ``Authorization hasRole matches singleton RepoScope allows`` (repoKeyRaw: string) (requiredRole: RepoRole) (assignedRole: RepoRole) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let requiredRepo = $"  {repoKey.ToUpperInvariant()}  "
    let scope = createRepoScope repoKey assignedRole
    let directAllows = RepoScope.allows requiredRepo requiredRole scope
    let authAllows = Authorization.hasRole [ scope ] requiredRepo requiredRole
    directAllows = authAllows

[<Property>]
let ``Authorization is monotonic when adding scopes`` (baseRepoRaw: string) (extraRepoRaw: string) (requiredRepoRaw: string) (baseRole: RepoRole) (extraRole: RepoRole) (requiredRole: RepoRole) =
    let baseScope = createRepoScope (sanitizeRepoKey baseRepoRaw) baseRole
    let extraScope = createRepoScope (sanitizeRepoKey extraRepoRaw) extraRole
    let requiredRepo = $"  {sanitizeRepoKey requiredRepoRaw |> fun value -> value.ToUpperInvariant()}  "

    let baseline = Authorization.hasRole [ baseScope ] requiredRepo requiredRole
    let expanded = Authorization.hasRole [ baseScope; extraScope ] requiredRepo requiredRole
    not baseline || expanded

[<Property>]
let ``Authorization wildcard admin scope grants all roles`` (repoKeyRaw: string) (requiredRole: RepoRole) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let wildcardAdmin = createRepoScope "*" RepoRole.Admin
    Authorization.hasRole [ wildcardAdmin ] repoKey requiredRole

[<Property>]
let ``API parseRoles normalizes casing and deduplicates`` (roles: NonEmptyArray<RepoRole>) =
    let rawValues =
        roles.Get
        |> Array.map (fun role -> $"  {RepoRole.value role |> fun value -> value.ToUpperInvariant()}  ")

    let expected = roles.Get |> Set.ofArray

    match Program.parseRoles rawValues with
    | Ok parsed -> parsed = expected
    | Error _ -> false

[<Property>]
let ``API parseScopes roundtrips valid repo scopes`` (repoKeyRaw: string) (roles: NonEmptyArray<RepoRole>) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let expectedScopes = roles.Get |> Array.map (createRepoScope repoKey) |> Array.toList
    let serialized = expectedScopes |> List.map RepoScope.value |> List.toArray

    match Program.parseScopes serialized with
    | Error _ -> false
    | Ok parsed ->
        (parsed |> List.map RepoScope.value) = (expectedScopes |> List.map RepoScope.value)

[<Property>]
let ``API validateUploadSessionRequest accepts valid digest and length`` (payload: byte array) (lengthSeed: int64) =
    let bytes = if isNull payload then Array.empty<byte> else payload
    let digest = SHA256.HashData(bytes) |> Convert.ToHexString
    let expectedLength = (normalizeNonNegativeInt64 lengthSeed % 1000000L) + 1L
    let request: Program.CreateUploadSessionRequest = { ExpectedDigest = digest; ExpectedLength = expectedLength }

    match Program.validateUploadSessionRequest request with
    | Error _ -> false
    | Ok (normalizedDigest, parsedLength) ->
        normalizedDigest = digest.ToLowerInvariant() && parsedLength = expectedLength

[<Property>]
let ``API validateUploadPartRequest accepts positive part number`` (partSeed: int) =
    let partNumber = (normalizeNonNegativeInt partSeed % 100000) + 1
    let request: Program.CreateUploadPartRequest = { PartNumber = partNumber }

    match Program.validateUploadPartRequest request with
    | Ok parsedPart -> parsedPart = partNumber
    | Error _ -> false

[<Property>]
let ``API validateUploadPartRequest rejects non-positive part number`` (partSeed: int) =
    let partNumber = -(normalizeNonNegativeInt partSeed % 100000)
    let request: Program.CreateUploadPartRequest = { PartNumber = partNumber }

    match Program.validateUploadPartRequest request with
    | Ok _ -> false
    | Error err -> err = "partNumber must be greater than zero."

[<Property>]
let ``API validateCompleteUploadRequest accepts unique positive parts and normalizes etag`` (partNumbers: NonEmptyArray<PositiveInt>) =
    let uniqueNumbers =
        partNumbers.Get
        |> Array.map (fun value -> value.Get)
        |> Array.distinct

    let parts: Program.UploadCompletedPartRequest array =
        uniqueNumbers
        |> Array.map (fun partNumber ->
            { PartNumber = partNumber
              ETag = $"\"etag-{partNumber}\"" })

    let request: Program.CompleteUploadPartsRequest = { Parts = parts }

    match Program.validateCompleteUploadRequest request with
    | Error _ -> false
    | Ok parsed ->
        parsed.Length = uniqueNumbers.Length
        && (parsed |> List.map (fun part -> part.PartNumber) = (uniqueNumbers |> Array.toList))
        && (parsed |> List.forall (fun part -> part.ETag = $"etag-{part.PartNumber}"))

[<Property>]
let ``API validateCompleteUploadRequest rejects duplicate part numbers`` (partNumber: PositiveInt) =
    let number = partNumber.Get
    let parts: Program.UploadCompletedPartRequest array =
        [| { PartNumber = number; ETag = "etag-a" }
           { PartNumber = number; ETag = "etag-b" } |]

    let request: Program.CompleteUploadPartsRequest =
        { Parts = parts }

    match Program.validateCompleteUploadRequest request with
    | Ok _ -> false
    | Error err -> err = $"Duplicate partNumber '{number}' is not allowed."

[<Property>]
let ``API parseSingleRangeHeader parses valid open range`` (startSeed: int64) =
    let startOffset = normalizeNonNegativeInt64 startSeed % 1000000L
    let rawRange = $" BYTES={startOffset}- "

    match Program.parseSingleRangeHeader rawRange with
    | Ok(Some(parsedStart, parsedEnd)) -> parsedStart = startOffset && parsedEnd.IsNone
    | _ -> false

[<Property>]
let ``API parseSingleRangeHeader parses valid bounded range`` (startSeed: int64) (lengthSeed: int64) =
    let startOffset = normalizeNonNegativeInt64 startSeed % 1000000L
    let rangeLength = normalizeNonNegativeInt64 lengthSeed % 1000000L
    let endOffset = startOffset + rangeLength
    let rawRange = $"bytes= {startOffset} - {endOffset} "

    match Program.parseSingleRangeHeader rawRange with
    | Ok(Some(parsedStart, Some parsedEnd)) -> parsedStart = startOffset && parsedEnd = endOffset
    | _ -> false

[<Property>]
let ``API parseSingleRangeHeader rejects end before start`` (startSeed: int64) =
    let startOffset = (normalizeNonNegativeInt64 startSeed % 1000000L) + 1L
    let endOffset = startOffset - 1L
    let rawRange = $"bytes={startOffset}-{endOffset}"

    match Program.parseSingleRangeHeader rawRange with
    | Ok _ -> false
    | Error err -> err = "Range end must be greater than or equal to range start."

[<Property>]
let ``API validateQuarantineStatusFilter accepts known values with casing and whitespace`` (indexSeed: int) =
    let allowed = [| "quarantined"; "released"; "rejected" |]
    let index = normalizeNonNegativeInt indexSeed % allowed.Length
    let expected = allowed.[index]
    let rawValue = $"  {expected.ToUpperInvariant()}  "

    match Program.validateQuarantineStatusFilter rawValue with
    | Ok(Some parsed) -> parsed = expected
    | _ -> false

[<Property>]
let ``API normalizeAbortReason defaults and trims`` (reasonRaw: string) =
    let normalized = Program.normalizeAbortReason reasonRaw

    if String.IsNullOrWhiteSpace reasonRaw then
        normalized = "client_abort"
    else
        normalized = reasonRaw.Trim()

[<Property>]
let ``API parseSingleRangeHeader rejects suffix-only ranges`` (suffixSeed: int64) =
    let suffixLength = (normalizeNonNegativeInt64 suffixSeed % 1000000L) + 1L
    let rawRange = $"bytes=-{suffixLength}"

    match Program.parseSingleRangeHeader rawRange with
    | Ok _ -> false
    | Error err -> err = "Suffix byte ranges are not supported."

[<Property>]
let ``API secureEquals follows exact non-empty equality semantics`` (leftRaw: string) (rightRaw: string) =
    let left = if isNull leftRaw then "" else leftRaw
    let right = if isNull rightRaw then "" else rightRaw
    let expected = not (String.IsNullOrEmpty left) && left = right
    Program.secureEquals leftRaw rightRaw = expected

[<Property>]
let ``API toTokenHash returns lowercase sha256 hex`` (tokenRaw: string) =
    let token = if isNull tokenRaw then "" else tokenRaw
    let hash = Program.toTokenHash token
    hash.Length = 64 && (hash |> Seq.forall isLowerHexCharacter)

[<Property>]
let ``API validateDraftVersionRequest normalizes package coordinates`` (typeRaw: string) (namespaceRaw: string) (nameRaw: string) (versionRaw: string) =
    let packageType =
        if String.IsNullOrWhiteSpace typeRaw then
            "NuGet"
        else
            typeRaw

    let packageName =
        if String.IsNullOrWhiteSpace nameRaw then
            "Core.Lib"
        else
            nameRaw

    let version =
        if String.IsNullOrWhiteSpace versionRaw then
            "1.0.0"
        else
            versionRaw

    let request: Program.CreateDraftVersionRequest =
        { PackageType = packageType
          PackageNamespace = namespaceRaw
          PackageName = packageName
          Version = version }

    match Program.validateDraftVersionRequest request with
    | Error _ -> false
    | Ok (normalizedType, normalizedNamespace, normalizedName, normalizedVersion) ->
        normalizedType = packageType.Trim().ToLowerInvariant()
        && normalizedName = packageName.Trim().ToLowerInvariant()
        && normalizedVersion = version.Trim()
        &&
        let expectedNamespace =
            if String.IsNullOrWhiteSpace namespaceRaw then
                None
            else
                Some(namespaceRaw.Trim().ToLowerInvariant())

        normalizedNamespace = expectedNamespace

[<Property>]
let ``ObjectStorage tryReadConfig applies defaults when settings are missing`` (paddingSeed: int) =
    let noiseValue = String.replicate (normalizeNonNegativeInt paddingSeed % 3) " "
    let values = Dictionary<string, string>()
    values["Unrelated"] <- noiseValue
    let config = ConfigurationBuilder().AddInMemoryCollection(values).Build()

    match ObjectStorage.tryReadConfig config with
    | Error _ -> false
    | Ok parsed ->
        parsed.Endpoint = "http://localhost:9000"
        && parsed.AccessKey = "artifortress"
        && parsed.SecretKey = "artifortress"
        && parsed.Bucket = "artifortress-dev"
        && parsed.PresignPartTtlSeconds = 900

[<Property>]
let ``ObjectStorage tryReadConfig accepts in-range presign ttl`` (ttlSeed: int) =
    let ttl = (normalizeNonNegativeInt ttlSeed % 3541) + 60
    let values = Dictionary<string, string>()
    values["ObjectStorage:PresignPartTtlSeconds"] <- string ttl
    let config = ConfigurationBuilder().AddInMemoryCollection(values).Build()

    match ObjectStorage.tryReadConfig config with
    | Error _ -> false
    | Ok parsed -> parsed.PresignPartTtlSeconds = ttl

[<Property>]
let ``ObjectStorage tryReadConfig falls back for out-of-range presign ttl`` (ttlSeed: int) (highRange: bool) =
    let ttl =
        if highRange then
            3601 + (normalizeNonNegativeInt ttlSeed % 10000)
        else
            normalizeNonNegativeInt ttlSeed % 60

    let values = Dictionary<string, string>()
    values["ObjectStorage:PresignPartTtlSeconds"] <- string ttl
    let config = ConfigurationBuilder().AddInMemoryCollection(values).Build()

    match ObjectStorage.tryReadConfig config with
    | Error _ -> false
    | Ok parsed -> parsed.PresignPartTtlSeconds = 900

[<Property>]
let ``API validateEvaluatePolicyRequest defaults blank decisionHint to allow`` (versionId: Guid) (actionSeed: bool) (reasonRaw: string) =
    if versionId = Guid.Empty then
        true
    else
        let action = if actionSeed then "publish" else "promote"
        let reason = if String.IsNullOrWhiteSpace reasonRaw then "policy-eval" else reasonRaw

        let request: Program.EvaluatePolicyRequest =
            { Action = $"  {action.ToUpperInvariant()}  "
              VersionId = versionId
              DecisionHint = "   "
              Reason = $"  {reason}  "
              PolicyEngineVersion = " v2 " }

        match Program.validateEvaluatePolicyRequest request with
        | Error _ -> false
        | Ok(parsedAction, parsedVersionId, decision, decisionSource, parsedReason, parsedEngineVersion) ->
            parsedAction = action
            && parsedVersionId = versionId
            && decision = "allow"
            && decisionSource = "default_allow"
            && parsedReason = reason.Trim()
            && parsedEngineVersion = Some "v2"

[<Property>]
let ``API validateEvaluatePolicyRequest maps explicit decision hints`` (versionId: Guid) (hintSeed: int) =
    if versionId = Guid.Empty then
        true
    else
        let hints = [| "allow", "hint_allow"; "deny", "hint_deny"; "quarantine", "hint_quarantine" |]
        let index = normalizeNonNegativeInt hintSeed % hints.Length
        let expectedDecision, expectedSource = hints.[index]

        let request: Program.EvaluatePolicyRequest =
            { Action = "publish"
              VersionId = versionId
              DecisionHint = $"  {expectedDecision.ToUpperInvariant()}  "
              Reason = "ok"
              PolicyEngineVersion = null }

        match Program.validateEvaluatePolicyRequest request with
        | Error _ -> false
        | Ok(_, _, decision, decisionSource, _, parsedEngineVersion) ->
            decision = expectedDecision && decisionSource = expectedSource && parsedEngineVersion.IsNone

[<Property>]
let ``API validateEvaluatePolicyRequest rejects unsupported decision hints`` (versionId: Guid) (hintRaw: string) =
    if versionId = Guid.Empty then
        true
    else
        let normalized =
            if String.IsNullOrWhiteSpace hintRaw then
                "block"
            else
                hintRaw.Trim().ToLowerInvariant()

        if normalized = "allow" || normalized = "deny" || normalized = "quarantine" then
            true
        else
            let invalidHint = if String.IsNullOrWhiteSpace hintRaw then "block" else hintRaw

            let request: Program.EvaluatePolicyRequest =
                { Action = "publish"
                  VersionId = versionId
                  DecisionHint = invalidHint
                  Reason = "reason"
                  PolicyEngineVersion = null }

            match Program.validateEvaluatePolicyRequest request with
            | Ok _ -> false
            | Error err -> err = "decisionHint must be one of: allow, deny, quarantine."

[<Property>]
let ``API validateRepoRequest local normalizes repo key`` (repoKeyRaw: string) =
    let repoKey = sanitizeRepoKey repoKeyRaw

    let request: Program.CreateRepoRequest =
        { RepoKey = $"  {repoKey.ToUpperInvariant()}  "
          RepoType = " LOCAL "
          UpstreamUrl = "https://ignored.example"
          MemberRepos = [| "ignored-one"; "ignored-two" |] }

    match Program.validateRepoRequest request with
    | Error _ -> false
    | Ok parsed ->
        parsed.RepoKey = repoKey
        && parsed.RepoType = "local"
        && parsed.UpstreamUrl.IsNone
        && parsed.MemberRepos.IsEmpty

[<Property>]
let ``API validateRepoRequest remote requires absolute upstream url and normalizes`` (repoKeyRaw: string) (pathRaw: string) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let pathSegment = sanitizeUriPathSegment pathRaw

    let upstream = $"https://example.org/{pathSegment}"

    let request: Program.CreateRepoRequest =
        { RepoKey = $" {repoKey} "
          RepoType = "remote"
          UpstreamUrl = $"  {upstream}  "
          MemberRepos = [||] }

    match Program.validateRepoRequest request with
    | Error _ -> false
    | Ok parsed ->
        parsed.RepoKey = repoKey
        && parsed.RepoType = "remote"
        && parsed.MemberRepos.IsEmpty
        && parsed.UpstreamUrl = Some(Uri(upstream).ToString())

[<Property>]
let ``API validateRepoRequest virtual normalizes distinct members`` (repoKeyRaw: string) (memberRaw: string) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let memberRepo = sanitizeRepoKey memberRaw

    let request: Program.CreateRepoRequest =
        { RepoKey = repoKey
          RepoType = "virtual"
          UpstreamUrl = ""
          MemberRepos = [| $"  {memberRepo.ToUpperInvariant()} "; memberRepo; "   "; $"{memberRepo}  " |] }

    match Program.validateRepoRequest request with
    | Error _ -> false
    | Ok parsed ->
        parsed.RepoType = "virtual"
        && parsed.UpstreamUrl.IsNone
        && parsed.MemberRepos = [ memberRepo ]

[<Property>]
let ``API validateRepoRequest rejects repo keys containing colon`` (leftRaw: string) (rightRaw: string) (repoTypeRaw: string) =
    let left = if String.IsNullOrWhiteSpace leftRaw then "left" else leftRaw.Trim()
    let right = if String.IsNullOrWhiteSpace rightRaw then "right" else rightRaw.Trim()
    let repoType = if String.IsNullOrWhiteSpace repoTypeRaw then "local" else repoTypeRaw

    let request: Program.CreateRepoRequest =
        { RepoKey = $"{left}:{right}"
          RepoType = repoType
          UpstreamUrl = "https://example.org"
          MemberRepos = [| "core" |] }

    match Program.validateRepoRequest request with
    | Ok _ -> false
    | Error err -> err = "repoKey cannot contain ':'."

[<Property>]
let ``API buildStagingObjectKey uses canonical path format`` (tenantId: Guid) (repoKeyRaw: string) (uploadId: Guid) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let key = Program.buildStagingObjectKey tenantId repoKey uploadId
    key = $"staging/{tenantId:N}/{repoKey}/{uploadId:N}"

[<Property(MaxTest = 20)>]
let ``API tryResolvePolicyDecisionWithTimeout returns timeout in simulate mode`` (decisionRaw: string) (sourceRaw: string) =
    let decision = if String.IsNullOrWhiteSpace decisionRaw then "allow" else decisionRaw.Trim()
    let source = if String.IsNullOrWhiteSpace sourceRaw then "default_allow" else sourceRaw.Trim()

    match Program.tryResolvePolicyDecisionWithTimeout 15 decision source (Some "simulate_timeout") with
    | Ok _ -> false
    | Error err -> err = "Policy evaluation timed out."

[<Property>]
let ``API tryResolvePolicyDecisionWithTimeout returns decision when not simulating timeout`` (decisionRaw: string) (sourceRaw: string) =
    let decision = if String.IsNullOrWhiteSpace decisionRaw then "allow" else decisionRaw.Trim()
    let source = if String.IsNullOrWhiteSpace sourceRaw then "default_allow" else sourceRaw.Trim()

    match Program.tryResolvePolicyDecisionWithTimeout 15 decision source (Some "engine-v1") with
    | Error _ -> false
    | Ok(parsedDecision, parsedSource) -> parsedDecision = decision && parsedSource = source

[<Property(MaxTest = 50)>]
let ``API createPlainToken returns lowercase hex token`` () =
    let token = Program.createPlainToken ()
    token.Length = 64 && (token |> Seq.forall isLowerHexCharacter)

[<Property>]
let ``API validateRepoRequest remote rejects relative upstream url`` (repoKeyRaw: string) (pathRaw: string) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let path = sanitizeUriPathSegment pathRaw

    let request: Program.CreateRepoRequest =
        { RepoKey = repoKey
          RepoType = "remote"
          UpstreamUrl = $"example.org/{path}"
          MemberRepos = [||] }

    match Program.validateRepoRequest request with
    | Ok _ -> false
    | Error err -> err = "upstreamUrl must be a valid absolute URI."

[<Property>]
let ``API validateRepoRequest virtual rejects empty normalized member list`` (repoKeyRaw: string) (whitespaceCount: int) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let blank = String.replicate (normalizeNonNegativeInt whitespaceCount % 4) " "

    let request: Program.CreateRepoRequest =
        { RepoKey = repoKey
          RepoType = "virtual"
          UpstreamUrl = ""
          MemberRepos = [| blank; "   "; "\t" |] }

    match Program.validateRepoRequest request with
    | Ok _ -> false
    | Error err -> err = "Virtual repositories require at least one member repo key."

[<Property>]
let ``API validateEvaluatePolicyRequest rejects invalid action`` (versionId: Guid) (actionRaw: string) =
    if versionId = Guid.Empty then
        true
    else
        let normalized =
            if String.IsNullOrWhiteSpace actionRaw then
                "deploy"
            else
                actionRaw.Trim().ToLowerInvariant()

        if normalized = "publish" || normalized = "promote" then
            true
        else
            let request: Program.EvaluatePolicyRequest =
                { Action = actionRaw
                  VersionId = versionId
                  DecisionHint = "allow"
                  Reason = "reason"
                  PolicyEngineVersion = null }

            match Program.validateEvaluatePolicyRequest request with
            | Ok _ -> false
            | Error err -> err = "action must be one of: publish, promote."

[<Property>]
let ``API validateEvaluatePolicyRequest rejects empty reason`` (versionId: Guid) (actionSeed: bool) =
    if versionId = Guid.Empty then
        true
    else
        let action = if actionSeed then "publish" else "promote"
        let request: Program.EvaluatePolicyRequest =
            { Action = action
              VersionId = versionId
              DecisionHint = "deny"
              Reason = "   "
              PolicyEngineVersion = null }

        match Program.validateEvaluatePolicyRequest request with
        | Ok _ -> false
        | Error err -> err = "reason is required."

[<Property>]
let ``API validateEvaluatePolicyRequest rejects empty version id`` (actionSeed: bool) =
    let action = if actionSeed then "publish" else "promote"
    let request: Program.EvaluatePolicyRequest =
        { Action = action
          VersionId = Guid.Empty
          DecisionHint = "allow"
          Reason = "reason"
          PolicyEngineVersion = null }

    match Program.validateEvaluatePolicyRequest request with
    | Ok _ -> false
    | Error err -> err = "versionId is required and must be a non-empty GUID."

[<Property>]
let ``API computeSha256AndLength matches standard hash and stream length`` (payload: byte array) =
    let bytes = if isNull payload then Array.empty<byte> else payload

    use stream = new IO.MemoryStream(bytes)
    let digest, length = Program.computeSha256AndLength stream
    let expectedDigest = SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    digest = expectedDigest && length = int64 bytes.Length

[<Property>]
let ``API parseRoles rejects null and empty role arrays`` (useNull: bool) =
    let input = if useNull then null else [||]

    match Program.parseRoles input with
    | Ok _ -> false
    | Error err -> err = "At least one role is required."

[<Property>]
let ``API parseScopes returns empty list for null scope array`` (useNull: bool) =
    let input = if useNull then null else [||]

    match Program.parseScopes input with
    | Error _ -> false
    | Ok scopes -> scopes.IsEmpty

[<Property>]
let ``API parseScopes reports errors when any scope is invalid`` (repoKeyRaw: string) =
    let repoKey = sanitizeRepoKey repoKeyRaw
    let valid = $"repo:{repoKey}:read"
    let invalid = "repo:bad-scope:superuser"

    match Program.parseScopes [| valid; invalid |] with
    | Ok _ -> false
    | Error err -> err.Contains("Unsupported role")

[<Property>]
let ``API parseSingleRangeHeader rejects multiple ranges`` (startSeed: int64) (deltaSeed: int64) =
    let start = normalizeNonNegativeInt64 startSeed % 100000L
    let delta = (normalizeNonNegativeInt64 deltaSeed % 100000L) + 1L
    let raw = $"bytes={start}-{start + delta},0-1"

    match Program.parseSingleRangeHeader raw with
    | Ok _ -> false
    | Error err -> err = "Multiple byte ranges are not supported."

[<Property>]
let ``API validateUploadSessionRequest rejects non-hex digest characters`` (lengthSeed: int64) (badCharSeed: int) =
    let candidates = [| "g"; "z"; "-"; "_" |]
    let bad = candidates.[normalizeNonNegativeInt badCharSeed % candidates.Length]
    let digest = String.replicate 63 "a" + bad
    let expectedLength = (normalizeNonNegativeInt64 lengthSeed % 1000000L) + 1L
    let request: Program.CreateUploadSessionRequest = { ExpectedDigest = digest; ExpectedLength = expectedLength }

    match Program.validateUploadSessionRequest request with
    | Ok _ -> false
    | Error err -> err = "expectedDigest must be a 64-character lowercase hex SHA-256 digest."

[<Property>]
let ``WorkerOutboxParsing resolves version id from aggregate id fast path`` (versionId: Guid) (payloadRaw: string) =
    let payload = if isNull payloadRaw then "" else payloadRaw
    match WorkerOutboxParsing.tryResolveVersionId (versionId.ToString("D")) payload with
    | Some parsed -> parsed = versionId
    | None -> false

[<Property>]
let ``WorkerOutboxParsing resolves version id from payload fallback path`` (versionId: Guid) (aggregateIdRaw: string) =
    let aggregateId =
        let value = if isNull aggregateIdRaw then "" else aggregateIdRaw.Trim()
        if String.IsNullOrWhiteSpace value then "not-a-guid" else $"{value}-x"

    let payload = $"{{\"versionId\":\"{versionId:D}\",\"eventType\":\"version.published\"}}"

    match WorkerOutboxParsing.tryResolveVersionId aggregateId payload with
    | Some parsed -> parsed = versionId
    | None -> false

[<Property>]
let ``WorkerOutboxParsing returns none for invalid aggregate and malformed payload`` (aggregateIdRaw: string) (payloadRaw: string) =
    let aggregateId =
        let value = if isNull aggregateIdRaw then "" else aggregateIdRaw.Trim()
        if String.IsNullOrWhiteSpace value then "invalid" else $"invalid-{value}"

    let payload =
        let value = if isNull payloadRaw then "" else payloadRaw
        $"{{\"versionId\":{value}}}"

    WorkerOutboxParsing.tryResolveVersionId aggregateId payload |> Option.isNone

[<Property>]
let ``WorkerRetryPolicy backoff seconds are monotonic by attempts`` (leftSeed: int) (rightSeed: int) =
    let left = (normalizeNonNegativeInt leftSeed % 5000) + 1
    let right = (normalizeNonNegativeInt rightSeed % 5000) + 1
    let low = min left right
    let high = max left right

    WorkerRetryPolicy.computeBackoffSeconds low <= WorkerRetryPolicy.computeBackoffSeconds high

[<Property>]
let ``WorkerRetryPolicy backoff seconds are capped after exponent ceiling`` (attemptSeed: int) =
    let attempts = (normalizeNonNegativeInt attemptSeed % 10000) + 7
    let capped = WorkerRetryPolicy.computeBackoffSeconds attempts
    let expectedCap = WorkerRetryPolicy.BaseDelaySeconds * (pown 2 WorkerRetryPolicy.MaxBackoffExponent)
    capped = expectedCap

[<Property>]
let ``WorkerRetryPolicy failure schedule increments attempts and is deterministic`` (currentAttemptsSeed: int) (secondsSeed: int64) =
    let currentAttempts = normalizeNonNegativeInt currentAttemptsSeed % 5000
    let referenceUtc = DateTimeOffset.UnixEpoch.AddSeconds(float (normalizeNonNegativeInt64 secondsSeed % 1000000L))
    let nextAttempts, nextAvailableUtc = WorkerRetryPolicy.computeFailureSchedule referenceUtc currentAttempts
    let expectedAttempts = currentAttempts + 1
    let expectedAvailable = WorkerRetryPolicy.computeNextAvailableUtc referenceUtc expectedAttempts
    nextAttempts = expectedAttempts && nextAvailableUtc = expectedAvailable

[<Property>]
let ``WorkerEnvParsing returns default for invalid or non-positive values`` (defaultSeed: PositiveInt) (rawSeed: int) (useNumeric: bool) =
    let defaultValue = (defaultSeed.Get % 1000) + 1
    let rawValue =
        if useNumeric then
            string (-(normalizeNonNegativeInt rawSeed % 100000))
        else
            "not-a-number"

    WorkerEnvParsing.parsePositiveIntOrDefault defaultValue rawValue = defaultValue

[<Property>]
let ``WorkerEnvParsing parses positive integer values`` (defaultSeed: PositiveInt) (valueSeed: int) =
    let defaultValue = (defaultSeed.Get % 1000) + 1
    let parsedValue = (normalizeNonNegativeInt valueSeed % 100000) + 1
    WorkerEnvParsing.parsePositiveIntOrDefault defaultValue (string parsedValue) = parsedValue

[<Property>]
let ``WorkerDbParameters normalizeBatchSize enforces minimum one`` (batchSeed: int) =
    let normalized = WorkerDbParameters.normalizeBatchSize batchSeed
    normalized >= 1 && (if batchSeed >= 1 then normalized = batchSeed else normalized = 1)

[<Property>]
let ``WorkerDbParameters normalizeMaxAttempts enforces minimum one`` (attemptSeed: int) =
    let normalized = WorkerDbParameters.normalizeMaxAttempts attemptSeed
    normalized >= 1 && (if attemptSeed >= 1 then normalized = attemptSeed else normalized = 1)

[<Property>]
let ``WorkerOutboxFlow prefers aggregate id guid over payload guid`` (aggregateVersionId: Guid) (payloadVersionId: Guid) =
    let payload = $"{{\"versionId\":\"{payloadVersionId:D}\"}}"

    match WorkerOutboxFlow.decideRouting (aggregateVersionId.ToString("D")) payload with
    | WorkerOutboxFlow.EnqueueVersion parsed -> parsed = aggregateVersionId
    | WorkerOutboxFlow.Requeue -> false

[<Property>]
let ``WorkerOutboxFlow falls back to payload guid when aggregate id is invalid`` (payloadVersionId: Guid) (aggregateRaw: string) =
    let aggregateId =
        let value = if isNull aggregateRaw then "" else aggregateRaw.Trim()
        if String.IsNullOrWhiteSpace value then "invalid" else $"invalid-{value}"

    let payload = $"{{\"versionId\":\"{payloadVersionId:D}\"}}"

    match WorkerOutboxFlow.decideRouting aggregateId payload with
    | WorkerOutboxFlow.EnqueueVersion parsed -> parsed = payloadVersionId
    | WorkerOutboxFlow.Requeue -> false

[<Property>]
let ``WorkerOutboxFlow requeues when no parseable version id exists`` (aggregateRaw: string) (payloadRaw: string) =
    let aggregateId =
        let value = if isNull aggregateRaw then "" else aggregateRaw.Trim()
        if String.IsNullOrWhiteSpace value then "bad" else $"bad-{value}"

    let payload =
        let value = if isNull payloadRaw then "" else payloadRaw.Replace("\"", "")
        $"{{\"versionId\":\"{value}-not-guid\"}}"

    WorkerOutboxFlow.decideRouting aggregateId payload = WorkerOutboxFlow.Requeue

[<Property>]
let ``WorkerJobFlow published job is marked complete`` (attemptSeed: int) (secondsSeed: int64) =
    let attempts = normalizeNonNegativeInt attemptSeed % 100000
    let nowUtc = DateTimeOffset.UnixEpoch.AddSeconds(float (normalizeNonNegativeInt64 secondsSeed % 1000000L))

    WorkerJobFlow.decideProcessing nowUtc attempts true = WorkerJobFlow.Complete

[<Property>]
let ``WorkerJobFlow unpublished job returns deterministic failure schedule`` (attemptSeed: int) (secondsSeed: int64) =
    let attempts = normalizeNonNegativeInt attemptSeed % 100000
    let nowUtc = DateTimeOffset.UnixEpoch.AddSeconds(float (normalizeNonNegativeInt64 secondsSeed % 1000000L))
    let expectedAttempts, expectedAvailableUtc = WorkerRetryPolicy.computeFailureSchedule nowUtc attempts

    match WorkerJobFlow.decideProcessing nowUtc attempts false with
    | WorkerJobFlow.Complete -> false
    | WorkerJobFlow.Fail(actualAttempts, actualAvailableUtc, errorMessage) ->
        actualAttempts = expectedAttempts
        && actualAvailableUtc = expectedAvailableUtc
        && errorMessage = "version_not_published"

[<Property>]
let ``WorkerJobFlow failure attempts are always positive`` (attemptSeed: int) (secondsSeed: int64) =
    let attempts = -((normalizeNonNegativeInt attemptSeed % 100000) + 1)
    let nowUtc = DateTimeOffset.UnixEpoch.AddSeconds(float (normalizeNonNegativeInt64 secondsSeed % 1000000L))

    match WorkerJobFlow.decideProcessing nowUtc attempts false with
    | WorkerJobFlow.Complete -> false
    | WorkerJobFlow.Fail(actualAttempts, _, _) -> actualAttempts >= 1

[<Property>]
let ``WorkerDataShapes claimed outbox constructor preserves fields`` (eventId: Guid) (tenantId: Guid) (aggregateIdRaw: string) (payloadRaw: string) =
    let aggregateId = if isNull aggregateIdRaw then "" else aggregateIdRaw
    let payload = if isNull payloadRaw then "" else payloadRaw
    let row = WorkerDataShapes.createClaimedOutboxEvent eventId tenantId aggregateId payload

    row.EventId = eventId
    && row.TenantId = tenantId
    && row.AggregateId = aggregateId
    && row.PayloadJson = payload

[<Property>]
let ``WorkerDataShapes claimed job constructor preserves fields`` (jobId: Guid) (tenantId: Guid) (versionId: Guid) (attemptSeed: int) =
    let attempts = attemptSeed
    let row = WorkerDataShapes.createClaimedSearchJob jobId tenantId versionId attempts

    row.JobId = jobId
    && row.TenantId = tenantId
    && row.VersionId = versionId
    && row.Attempts = attempts

[<Property>]
let ``WorkerSweepMetrics recordRequeue increments only requeue counter`` (enqSeed: int) (delSeed: int) (reqSeed: int) =
    let start: WorkerSweepMetrics.OutboxMetrics =
        { EnqueuedCount = normalizeNonNegativeInt enqSeed % 1000
          DeliveredCount = normalizeNonNegativeInt delSeed % 1000
          RequeuedCount = normalizeNonNegativeInt reqSeed % 1000 }

    let updated = WorkerSweepMetrics.recordRequeue start
    updated.EnqueuedCount = start.EnqueuedCount
    && updated.DeliveredCount = start.DeliveredCount
    && updated.RequeuedCount = start.RequeuedCount + 1

[<Property>]
let ``WorkerSweepMetrics recordEnqueue increments delivered conditionally`` (enqSeed: int) (delSeed: int) (reqSeed: int) (wasDelivered: bool) =
    let start: WorkerSweepMetrics.OutboxMetrics =
        { EnqueuedCount = normalizeNonNegativeInt enqSeed % 1000
          DeliveredCount = normalizeNonNegativeInt delSeed % 1000
          RequeuedCount = normalizeNonNegativeInt reqSeed % 1000 }

    let updated = WorkerSweepMetrics.recordEnqueue wasDelivered start
    updated.EnqueuedCount = start.EnqueuedCount + 1
    && updated.RequeuedCount = start.RequeuedCount
    && updated.DeliveredCount = start.DeliveredCount + if wasDelivered then 1 else 0

[<Property>]
let ``WorkerSweepMetrics outbox fold matches expected counters`` (steps: int array) =
    let values = if isNull steps then Array.empty<int> else steps

    let expectedEnqueued =
        values
        |> Array.sumBy (fun step ->
            let normalized = normalizeNonNegativeInt step % 3
            if normalized = 1 || normalized = 2 then 1 else 0)

    let expectedDelivered =
        values
        |> Array.sumBy (fun step ->
            let normalized = normalizeNonNegativeInt step % 3
            if normalized = 2 then 1 else 0)

    let expectedRequeued =
        values
        |> Array.sumBy (fun step ->
            let normalized = normalizeNonNegativeInt step % 3
            if normalized = 0 then 1 else 0)

    let actual =
        values
        |> Array.fold
            (fun metrics step ->
                let normalized = normalizeNonNegativeInt step % 3

                match normalized with
                | 0 -> WorkerSweepMetrics.recordRequeue metrics
                | 1 -> WorkerSweepMetrics.recordEnqueue false metrics
                | _ -> WorkerSweepMetrics.recordEnqueue true metrics)
            WorkerSweepMetrics.zeroOutbox

    actual.EnqueuedCount = expectedEnqueued
    && actual.DeliveredCount = expectedDelivered
    && actual.RequeuedCount = expectedRequeued

[<Property>]
let ``WorkerSweepMetrics job fold matches expected counters`` (steps: bool array) =
    let values = if isNull steps then Array.empty<bool> else steps

    let expectedCompleted = values |> Array.sumBy (fun isCompleted -> if isCompleted then 1 else 0)
    let expectedFailed = values.Length - expectedCompleted

    let actual =
        values
        |> Array.fold
            (fun metrics isCompleted ->
                if isCompleted then
                    WorkerSweepMetrics.recordCompleted metrics
                else
                    WorkerSweepMetrics.recordFailed metrics)
            WorkerSweepMetrics.zeroJobs

    actual.CompletedCount = expectedCompleted && actual.FailedCount = expectedFailed
