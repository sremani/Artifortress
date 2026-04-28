namespace Artifortress

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading

module AdminCli =
    type AuthMode =
        | NoAuth
        | Bearer of string
        | Bootstrap of string

    type RequestPlan = {
        Method: string
        PathAndQuery: string
        BodyJson: string option
        AuthMode: AuthMode
        CorrelationId: string option
        GovernanceApprovalId: string option
    }

    type CommandPlan =
        | Single of RequestPlan
        | Preflight of ready: RequestPlan * opsSummary: RequestPlan

    type ParsedOptions = {
        BaseUrl: string
        Token: string option
        BootstrapToken: string option
        CorrelationId: string option
        GovernanceApprovalId: string option
        Args: string list
    }

    let private jsonOptions =
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        )

    let private usage =
        """Artifortress admin CLI

Usage:
  artifortress-admin --url <api-url> [--token <pat-or-federated-token>] <command>
  ARTIFORTRESS_URL and ARTIFORTRESS_TOKEN may be used instead of --url/--token.

Global options:
  --url <url>                    API base URL. Default: ARTIFORTRESS_URL or http://localhost:5000
  --token <token>                Bearer token. Default: ARTIFORTRESS_TOKEN
  --bootstrap-token <token>      Bootstrap token for initial PAT issue.
  --correlation-id <guid>        X-Correlation-Id header.
  --approval-id <guid>           X-Governance-Approval-Id header.

Commands:
  health live|ready
  preflight
  ops summary
  auth whoami
  auth issue-pat --subject <subject> --scope <scope>... --ttl-minutes <minutes>
  auth revoke-pat --token-id <guid>
  auth pats list
  auth pats revoke-subject --subject <subject> [--reason <reason>]
  auth pats revoke-all [--reason <reason>]
  auth pat-policy get
  auth pat-policy set --max-ttl-minutes <minutes> --allow-bootstrap-issuance true|false
  tenant roles list
  tenant roles set --subject <subject> --role <role>...
  tenant roles delete --subject <subject>
  tenant admission get|set --max-logical-storage-bytes <n> --max-concurrent-upload-sessions <n> --max-pending-search-jobs <n>
  tenant governance get|set --min-tombstone-retention-days <n> --dual-control-tombstone true|false --dual-control-quarantine true|false
  tenant lifecycle mark --step <step> [--status planned|started|completed|blocked] [--subject <subject>] [--repo <repoKey>] [--reason <reason>] [--retention-until-utc <timestamp>]
  tenant offboarding-readiness
  repo list|get|delete --repo <repoKey>
  repo create --repo <repoKey> --type local|remote|virtual [--upstream-url <url>] [--member-repo <repoKey>]...
  repo bindings list --repo <repoKey>
  repo bindings set --repo <repoKey> --subject <subject> --role <role>...
  repo bindings delete --repo <repoKey> --subject <subject>
  repo governance get|set --repo <repoKey> [--min-tombstone-retention-days <n>] [--dual-control-tombstone true|false] [--dual-control-quarantine true|false]
  repo approvals list|create|approve --repo <repoKey> ...
  repo protect-version --repo <repoKey> --version-id <guid> --mode protected|legal_hold --reason <reason>
  repo release-protection --repo <repoKey> --version-id <guid> --reason <reason>
  repo tombstone-version --repo <repoKey> --version-id <guid> --reason <reason> [--retention-days <n>]
  search status|rebuild|pause|resume|cancel [--repo <repoKey>] [--batch-size <n>] [--reason <reason>]
  gc run [--execute] [--retention-grace-hours <n>] [--batch-size <n>]
  reconcile blobs
  compliance legal-holds
  compliance evidence [--audit-limit <n>] [--approval-limit <n>]
  audit list|export [--limit <n>] [--action <action>] [--subject <subject>] [--repo <repoKey>]
"""

    let private trimSlash (value: string) = value.TrimEnd('/')

    let private normalizeOptionName (value: string) =
        value.Trim().ToLowerInvariant()

    let private getEnv name =
        let value = Environment.GetEnvironmentVariable(name)
        if String.IsNullOrWhiteSpace value then None else Some value

    let private addQuery (pairs: (string * string option) list) =
        let encoded =
            pairs
            |> List.choose (fun (name, valueOption) ->
                valueOption
                |> Option.bind (fun value ->
                    if String.IsNullOrWhiteSpace value then
                        None
                    else
                        Some($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}")))

        match encoded with
        | [] -> ""
        | values -> "?" + String.Join("&", values)

    let private parseBool (field: string) (value: string) =
        match Boolean.TryParse(value) with
        | true, parsed -> Ok parsed
        | _ -> Error $"{field} must be true or false."

    let private parseInt (field: string) (value: string) =
        match Int32.TryParse(value) with
        | true, parsed -> Ok parsed
        | _ -> Error $"{field} must be an integer."

    let private parseInt64 (field: string) (value: string) =
        match Int64.TryParse(value) with
        | true, parsed -> Ok parsed
        | _ -> Error $"{field} must be an integer."

    let private serialize value = JsonSerializer.Serialize(value, jsonOptions)

    let private collectGlobalOptions (argv: string array) =
        let rec loop remaining options =
            match remaining with
            | [] -> Ok { options with Args = List.rev options.Args }
            | "--help" :: _
            | "-h" :: _ -> Error usage
            | "--url" :: value :: tail -> loop tail { options with BaseUrl = value }
            | "--token" :: value :: tail -> loop tail { options with Token = Some value }
            | "--bootstrap-token" :: value :: tail -> loop tail { options with BootstrapToken = Some value }
            | "--correlation-id" :: value :: tail -> loop tail { options with CorrelationId = Some value }
            | "--approval-id" :: value :: tail -> loop tail { options with GovernanceApprovalId = Some value }
            | option :: _ when option.StartsWith("--", StringComparison.Ordinal) ->
                Error $"Unknown global option '{option}'."
            | _ -> Ok { options with Args = remaining }

        let defaults =
            { BaseUrl = getEnv "ARTIFORTRESS_URL" |> Option.defaultValue "http://localhost:5000"
              Token = getEnv "ARTIFORTRESS_TOKEN"
              BootstrapToken = getEnv "ARTIFORTRESS_BOOTSTRAP_TOKEN"
              CorrelationId = getEnv "ARTIFORTRESS_CORRELATION_ID"
              GovernanceApprovalId = getEnv "ARTIFORTRESS_GOVERNANCE_APPROVAL_ID"
              Args = [] }

        loop (Array.toList argv) defaults

    let private parseCommandOptions (args: string list) =
        let values = Dictionary<string, ResizeArray<string>>(StringComparer.OrdinalIgnoreCase)
        let flags = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let positionals = ResizeArray<string>()

        let rec loop (remaining: string list) =
            match remaining with
            | [] -> Ok()
            | raw :: tail when raw.StartsWith("--", StringComparison.Ordinal) ->
                let name = normalizeOptionName raw

                match tail with
                | value :: rest when not (value.StartsWith("--", StringComparison.Ordinal)) ->
                    if not (values.ContainsKey name) then
                        values.[name] <- ResizeArray<string>()

                    values.[name].Add(value)
                    loop rest
                | _ ->
                    flags.Add(name) |> ignore
                    loop tail
            | value :: tail ->
                positionals.Add(value)
                loop tail

        let one name =
            match values.TryGetValue(name) with
            | true, found when found.Count > 0 -> Some found.[found.Count - 1]
            | _ -> None

        let many name =
            match values.TryGetValue(name) with
            | true, found -> found |> Seq.toList
            | _ -> []

        loop args
        |> Result.map (fun () ->
            positionals |> Seq.toList,
            one,
            many,
            fun name -> flags.Contains(name))

    let private requireOption name (one: string -> string option) =
        match one name with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"{name} is required."

    let private optionalInt name one =
        match one name with
        | Some value -> parseInt name value |> Result.map Some
        | None -> Ok None

    let private optionalBool name one =
        match one name with
        | Some value -> parseBool name value |> Result.map Some
        | None -> Ok None

    let private bearer options =
        match options.Token with
        | Some token -> Bearer token
        | None -> Bearer ""

    let private bootstrapOrBearer options =
        match options.BootstrapToken with
        | Some token -> Bootstrap token
        | None -> bearer options

    let private request options method path body auth =
        { Method = method
          PathAndQuery = path
          BodyJson = body
          AuthMode = auth
          CorrelationId = options.CorrelationId
          GovernanceApprovalId = options.GovernanceApprovalId }

    let private buildPlanFromParsed options =
        parseCommandOptions options.Args
        |> Result.bind (fun (positionals, one, many, hasFlag) ->
            match positionals with
            | [ "health"; "live" ] -> Ok(Single(request options "GET" "/health/live" None NoAuth))
            | [ "health"; "ready" ] -> Ok(Single(request options "GET" "/health/ready" None NoAuth))
            | [ "preflight" ] ->
                let ready = request options "GET" "/health/ready" None NoAuth
                let summary = request options "GET" "/v1/admin/ops/summary" None (bearer options)
                Ok(Preflight(ready, summary))
            | [ "ops"; "summary" ] -> Ok(Single(request options "GET" "/v1/admin/ops/summary" None (bearer options)))
            | [ "auth"; "whoami" ] -> Ok(Single(request options "GET" "/v1/auth/whoami" None (bearer options)))
            | [ "auth"; "issue-pat" ] ->
                requireOption "--subject" one
                |> Result.bind (fun subject ->
                    requireOption "--ttl-minutes" one
                    |> Result.bind (parseInt "--ttl-minutes")
                    |> Result.map (fun ttlMinutes ->
                        let scopes = many "--scope" |> List.toArray
                        {| subject = subject; scopes = scopes; ttlMinutes = ttlMinutes |}))
                |> Result.map (fun body ->
                    Single(request options "POST" "/v1/auth/pats" (Some(serialize body)) (bootstrapOrBearer options)))
            | [ "auth"; "revoke-pat" ] ->
                requireOption "--token-id" one
                |> Result.map (fun tokenId ->
                    Single(request options "POST" "/v1/auth/pats/revoke" (Some(serialize {| tokenId = tokenId |})) (bearer options)))
            | [ "auth"; "pats"; "list" ] ->
                Ok(Single(request options "GET" "/v1/admin/auth/pats" None (bearer options)))
            | [ "auth"; "pats"; "revoke-subject" ] ->
                requireOption "--subject" one
                |> Result.map (fun subject ->
                    let reason = one "--reason" |> Option.defaultValue "operator_revocation"
                    let body = serialize {| subject = subject; reason = reason |}
                    Single(request options "POST" "/v1/admin/auth/pats/revoke-subject" (Some body) (bearer options)))
            | [ "auth"; "pats"; "revoke-all" ] ->
                let reason = one "--reason" |> Option.defaultValue "operator_revocation"
                Ok(Single(request options "POST" "/v1/admin/auth/pats/revoke-all" (Some(serialize {| reason = reason |})) (bearer options)))
            | [ "auth"; "pat-policy"; "get" ] ->
                Ok(Single(request options "GET" "/v1/admin/auth/pat-policy" None (bearer options)))
            | [ "auth"; "pat-policy"; "set" ] ->
                requireOption "--max-ttl-minutes" one
                |> Result.bind (parseInt "--max-ttl-minutes")
                |> Result.bind (fun maxTtl ->
                    requireOption "--allow-bootstrap-issuance" one
                    |> Result.bind (parseBool "--allow-bootstrap-issuance")
                    |> Result.map (fun allowBootstrap ->
                        {| maxTtlMinutes = maxTtl
                           allowBootstrapIssuance = allowBootstrap |}))
                |> Result.map (fun body ->
                    Single(request options "PUT" "/v1/admin/auth/pat-policy" (Some(serialize body)) (bearer options)))
            | [ "tenant"; "roles"; "list" ] ->
                Ok(Single(request options "GET" "/v1/admin/tenant-role-bindings" None (bearer options)))
            | [ "tenant"; "roles"; "set" ] ->
                requireOption "--subject" one
                |> Result.map (fun subject ->
                    let roles = many "--role" |> List.toArray
                    let path = $"/v1/admin/tenant-role-bindings/{Uri.EscapeDataString(subject)}"
                    Single(request options "PUT" path (Some(serialize {| roles = roles |})) (bearer options)))
            | [ "tenant"; "roles"; "delete" ] ->
                requireOption "--subject" one
                |> Result.map (fun subject ->
                    let path = $"/v1/admin/tenant-role-bindings/{Uri.EscapeDataString(subject)}"
                    Single(request options "DELETE" path None (bearer options)))
            | [ "tenant"; "admission"; "get" ] ->
                Ok(Single(request options "GET" "/v1/admin/tenant-admission-policy" None (bearer options)))
            | [ "tenant"; "admission"; "set" ] ->
                requireOption "--max-logical-storage-bytes" one
                |> Result.bind (parseInt64 "--max-logical-storage-bytes")
                |> Result.bind (fun storage ->
                    requireOption "--max-concurrent-upload-sessions" one
                    |> Result.bind (parseInt "--max-concurrent-upload-sessions")
                    |> Result.bind (fun uploads ->
                        requireOption "--max-pending-search-jobs" one
                        |> Result.bind (parseInt "--max-pending-search-jobs")
                        |> Result.map (fun jobs ->
                            {| maxLogicalStorageBytes = storage
                               maxConcurrentUploadSessions = uploads
                               maxPendingSearchJobs = jobs |})))
                |> Result.map (fun body ->
                    Single(request options "PUT" "/v1/admin/tenant-admission-policy" (Some(serialize body)) (bearer options)))
            | [ "tenant"; "governance"; "get" ] ->
                Ok(Single(request options "GET" "/v1/admin/governance/policy" None (bearer options)))
            | [ "tenant"; "governance"; "set" ] ->
                requireOption "--min-tombstone-retention-days" one
                |> Result.bind (parseInt "--min-tombstone-retention-days")
                |> Result.bind (fun days ->
                    requireOption "--dual-control-tombstone" one
                    |> Result.bind (parseBool "--dual-control-tombstone")
                    |> Result.bind (fun tombstone ->
                        requireOption "--dual-control-quarantine" one
                        |> Result.bind (parseBool "--dual-control-quarantine")
                        |> Result.map (fun quarantine ->
                            {| minTombstoneRetentionDays = days
                               requireDualControlForTombstone = tombstone
                               requireDualControlForQuarantineResolution = quarantine |})))
                |> Result.map (fun body ->
                    Single(request options "PUT" "/v1/admin/governance/policy" (Some(serialize body)) (bearer options)))
            | [ "tenant"; "lifecycle"; "mark" ] ->
                requireOption "--step" one
                |> Result.map (fun step ->
                    {| step = step
                       status = one "--status" |> Option.defaultValue "completed"
                       subject = one "--subject" |> Option.defaultValue ""
                       repoKey = one "--repo" |> Option.defaultValue ""
                       reason = one "--reason" |> Option.defaultValue ""
                       retentionUntilUtc = one "--retention-until-utc" |> Option.defaultValue "" |})
                |> Result.map (fun body ->
                    Single(request options "POST" "/v1/admin/tenant-lifecycle/events" (Some(serialize body)) (bearer options)))
            | [ "tenant"; "offboarding-readiness" ] ->
                Ok(Single(request options "GET" "/v1/admin/tenant-lifecycle/offboarding-readiness" None (bearer options)))
            | [ "repo"; "list" ] -> Ok(Single(request options "GET" "/v1/repos" None (bearer options)))
            | [ "repo"; "get" ] ->
                requireOption "--repo" one
                |> Result.map (fun repo -> Single(request options "GET" $"/v1/repos/{Uri.EscapeDataString(repo)}" None (bearer options)))
            | [ "repo"; "delete" ] ->
                requireOption "--repo" one
                |> Result.map (fun repo -> Single(request options "DELETE" $"/v1/repos/{Uri.EscapeDataString(repo)}" None (bearer options)))
            | [ "repo"; "create" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    requireOption "--type" one
                    |> Result.map (fun repoType ->
                        {| repoKey = repo
                           repoType = repoType
                           upstreamUrl = one "--upstream-url" |> Option.defaultValue ""
                           memberRepos = many "--member-repo" |> List.toArray |}))
                |> Result.map (fun body -> Single(request options "POST" "/v1/repos" (Some(serialize body)) (bearer options)))
            | [ "repo"; "bindings"; "list" ] ->
                requireOption "--repo" one
                |> Result.map (fun repo ->
                    Single(request options "GET" $"/v1/repos/{Uri.EscapeDataString(repo)}/bindings" None (bearer options)))
            | [ "repo"; "bindings"; "set" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    requireOption "--subject" one
                    |> Result.map (fun subject -> repo, subject))
                |> Result.map (fun (repo, subject) ->
                    let path = $"/v1/repos/{Uri.EscapeDataString(repo)}/bindings/{Uri.EscapeDataString(subject)}"
                    let body = serialize {| roles = many "--role" |> List.toArray |}
                    Single(request options "PUT" path (Some body) (bearer options)))
            | [ "repo"; "bindings"; "delete" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    requireOption "--subject" one
                    |> Result.map (fun subject -> repo, subject))
                |> Result.map (fun (repo, subject) ->
                    let path = $"/v1/repos/{Uri.EscapeDataString(repo)}/bindings/{Uri.EscapeDataString(subject)}"
                    Single(request options "DELETE" path None (bearer options)))
            | [ "repo"; "governance"; "get" ] ->
                requireOption "--repo" one
                |> Result.map (fun repo ->
                    Single(request options "GET" $"/v1/repos/{Uri.EscapeDataString(repo)}/governance/policy" None (bearer options)))
            | [ "repo"; "governance"; "set" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    optionalInt "--min-tombstone-retention-days" one
                    |> Result.bind (fun days ->
                        optionalBool "--dual-control-tombstone" one
                        |> Result.bind (fun tombstone ->
                            optionalBool "--dual-control-quarantine" one
                            |> Result.map (fun quarantine -> repo, days, tombstone, quarantine))))
                |> Result.map (fun (repo, days, tombstone, quarantine) ->
                    let body =
                        serialize
                            {| minTombstoneRetentionDays = days |> Option.toNullable
                               requireDualControlForTombstone = tombstone |> Option.toNullable
                               requireDualControlForQuarantineResolution = quarantine |> Option.toNullable |}

                    Single(request options "PUT" $"/v1/repos/{Uri.EscapeDataString(repo)}/governance/policy" (Some body) (bearer options)))
            | [ "repo"; "approvals"; "list" ] ->
                requireOption "--repo" one
                |> Result.map (fun repo ->
                    Single(request options "GET" $"/v1/repos/{Uri.EscapeDataString(repo)}/approvals" None (bearer options)))
            | [ "repo"; "approvals"; "create" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    requireOption "--action" one
                    |> Result.bind (fun action ->
                        requireOption "--resource-type" one
                        |> Result.bind (fun resourceType ->
                            requireOption "--resource-id" one
                            |> Result.bind (fun resourceId ->
                                requireOption "--justification" one
                                |> Result.map (fun justification -> repo, action, resourceType, resourceId, justification)))))
                |> Result.map (fun (repo, action, resourceType, resourceId, justification) ->
                    let body =
                        serialize
                            {| action = action
                               resourceType = resourceType
                               resourceId = resourceId
                               justification = justification |}

                    Single(request options "POST" $"/v1/repos/{Uri.EscapeDataString(repo)}/approvals" (Some body) (bearer options)))
            | [ "repo"; "approvals"; "approve" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    requireOption "--approval-id" one |> Result.map (fun approvalId -> repo, approvalId))
                |> Result.map (fun (repo, approvalId) ->
                    Single(
                        request
                            options
                            "POST"
                            $"/v1/repos/{Uri.EscapeDataString(repo)}/approvals/{Uri.EscapeDataString(approvalId)}/approve"
                            None
                            (bearer options)
                    ))
            | [ "repo"; "protect-version" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    requireOption "--version-id" one
                    |> Result.bind (fun versionId ->
                        requireOption "--mode" one
                        |> Result.bind (fun mode ->
                            requireOption "--reason" one
                            |> Result.map (fun reason -> repo, versionId, mode, reason))))
                |> Result.map (fun (repo, versionId, mode, reason) ->
                    Single(
                        request
                            options
                            "POST"
                            $"/v1/repos/{Uri.EscapeDataString(repo)}/packages/versions/{Uri.EscapeDataString(versionId)}/protection"
                            (Some(serialize {| mode = mode; reason = reason |}))
                            (bearer options)
                    ))
            | [ "repo"; "release-protection" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    requireOption "--version-id" one
                    |> Result.bind (fun versionId ->
                        requireOption "--reason" one |> Result.map (fun reason -> repo, versionId, reason)))
                |> Result.map (fun (repo, versionId, reason) ->
                    Single(
                        request
                            options
                            "POST"
                            $"/v1/repos/{Uri.EscapeDataString(repo)}/packages/versions/{Uri.EscapeDataString(versionId)}/protection/release"
                            (Some(serialize {| reason = reason |}))
                            (bearer options)
                    ))
            | [ "repo"; "tombstone-version" ] ->
                requireOption "--repo" one
                |> Result.bind (fun repo ->
                    requireOption "--version-id" one
                    |> Result.bind (fun versionId ->
                        requireOption "--reason" one
                        |> Result.bind (fun reason ->
                            optionalInt "--retention-days" one
                            |> Result.map (fun retentionDays -> repo, versionId, reason, retentionDays))))
                |> Result.map (fun (repo, versionId, reason, retentionDays) ->
                    let body = serialize {| reason = reason; retentionDays = retentionDays |> Option.defaultValue 0 |}

                    Single(
                        request
                            options
                            "POST"
                            $"/v1/repos/{Uri.EscapeDataString(repo)}/packages/versions/{Uri.EscapeDataString(versionId)}/tombstone"
                            (Some body)
                            (bearer options)
                    ))
            | [ "search"; "status" ] ->
                let query = addQuery [ "repoKey", one "--repo" ]
                Ok(Single(request options "GET" $"/v1/admin/search/status{query}" None (bearer options)))
            | [ "search"; "rebuild" ] ->
                let body =
                    serialize
                        {| repoKey = one "--repo" |> Option.defaultValue ""
                           batchSize = one "--batch-size" |> Option.bind (fun v -> match Int32.TryParse(v) with | true, p -> Some p | _ -> None) |> Option.defaultValue 0 |}

                Ok(Single(request options "POST" "/v1/admin/search/rebuild" (Some body) (bearer options)))
            | [ "search"; "pause" ] ->
                requireOption "--reason" one
                |> Result.map (fun reason ->
                    Single(request options "POST" "/v1/admin/search/pause" (Some(serialize {| reason = reason |})) (bearer options)))
            | [ "search"; "resume" ] ->
                Ok(Single(request options "POST" "/v1/admin/search/resume" None (bearer options)))
            | [ "search"; "cancel" ] ->
                let body =
                    serialize
                        {| repoKey = one "--repo" |> Option.defaultValue ""
                           reason = one "--reason" |> Option.defaultValue "operator_cancelled" |}

                Ok(Single(request options "POST" "/v1/admin/search/cancel" (Some body) (bearer options)))
            | [ "gc"; "run" ] ->
                let retentionGraceHours = one "--retention-grace-hours" |> Option.bind (fun v -> match Int32.TryParse(v) with | true, p -> Some p | _ -> None) |> Option.defaultValue 0
                let batchSize = one "--batch-size" |> Option.bind (fun v -> match Int32.TryParse(v) with | true, p -> Some p | _ -> None) |> Option.defaultValue 0
                let body =
                    serialize
                        {| dryRun = not (hasFlag "--execute")
                           retentionGraceHours = retentionGraceHours
                           batchSize = batchSize |}

                Ok(Single(request options "POST" "/v1/admin/gc/runs" (Some body) (bearer options)))
            | [ "reconcile"; "blobs" ] ->
                Ok(Single(request options "GET" "/v1/admin/reconcile/blobs" None (bearer options)))
            | [ "compliance"; "legal-holds" ] ->
                Ok(Single(request options "GET" "/v1/compliance/legal-holds" None (bearer options)))
            | [ "compliance"; "evidence" ] ->
                let query =
                    addQuery
                        [ "auditLimit", one "--audit-limit"
                          "approvalLimit", one "--approval-limit" ]

                Ok(Single(request options "GET" $"/v1/compliance/evidence{query}" None (bearer options)))
            | [ "audit"; "list" ]
            | [ "audit"; "export" ] ->
                let endpoint =
                    match positionals with
                    | [ "audit"; "export" ] -> "/v1/audit/export"
                    | _ -> "/v1/audit"

                let query =
                    addQuery
                        [ "limit", one "--limit"
                          "action", one "--action"
                          "subject", one "--subject"
                          "repoKey", one "--repo" ]

                Ok(Single(request options "GET" $"{endpoint}{query}" None (bearer options)))
            | [] -> Error usage
            | _ ->
                let commandText = String.Join(" ", positionals)
                Error $"Unsupported command: {commandText}")

    let buildPlan argv =
        collectGlobalOptions argv |> Result.bind buildPlanFromParsed

    let private redactTokenLikeText (value: string) =
        if String.IsNullOrWhiteSpace value then
            value
        else
            let patterns =
                [ ("""(?i)(authorization\s*:\s*bearer\s+)([A-Za-z0-9\-\._~\+/=]+)""", "$1[REDACTED]")
                  ("""(?i)(bearer\s+)([A-Za-z0-9\-\._~\+/=]+)""", "$1[REDACTED]")
                  ("""(?i)("(?:token|secret|password|authorization|samlresponse)"\s*:\s*")([^"]+)(")""", "$1[REDACTED]$3") ]

            patterns
            |> List.fold (fun current (pattern, replacement) -> Regex.Replace(current, pattern, replacement)) value

    let rec private redactJsonNode (node: JsonNode) =
        match node with
        | :? JsonObject as obj ->
            let keys = obj |> Seq.map (fun kvp -> kvp.Key) |> Seq.toList

            for key in keys do
                let lowerKey = key.ToLowerInvariant()
                let isSensitive =
                    (lowerKey.Contains("token")
                     || lowerKey.Contains("secret")
                     || lowerKey.Contains("password")
                     || lowerKey.Contains("authorization")
                     || lowerKey.Contains("samlresponse"))
                    && not (lowerKey = "tokenid" || lowerKey = "token_id")

                if isSensitive then
                    obj.[key] <- JsonValue.Create("[REDACTED]")
                else
                    match obj.[key] with
                    | null -> ()
                    | child -> redactJsonNode child
        | :? JsonArray as arr ->
            for child in arr do
                if not (isNull child) then
                    redactJsonNode child
        | _ -> ()

    let redactOutput text =
        if String.IsNullOrWhiteSpace text then
            text
        else
            try
                let node = JsonNode.Parse(text)

                if isNull node then
                    redactTokenLikeText text
                else
                    redactJsonNode node
                    node.ToJsonString(jsonOptions)
            with _ ->
                redactTokenLikeText text

    let private applyHeaders (requestMessage: HttpRequestMessage) plan =
        match plan.AuthMode with
        | NoAuth -> ()
        | Bearer token when not (String.IsNullOrWhiteSpace token) ->
            requestMessage.Headers.Authorization <- Net.Http.Headers.AuthenticationHeaderValue("Bearer", token)
        | Bootstrap token when not (String.IsNullOrWhiteSpace token) ->
            requestMessage.Headers.Add("X-Bootstrap-Token", token)
        | _ -> ()

        plan.CorrelationId |> Option.iter (fun value -> requestMessage.Headers.Add("X-Correlation-Id", value))
        plan.GovernanceApprovalId |> Option.iter (fun value -> requestMessage.Headers.Add("X-Governance-Approval-Id", value))

    let private sendRequest (client: HttpClient) baseUrl plan =
        task {
            use requestMessage =
                new HttpRequestMessage(HttpMethod(plan.Method), Uri($"{trimSlash baseUrl}{plan.PathAndQuery}"))

            applyHeaders requestMessage plan

            match plan.BodyJson with
            | Some body -> requestMessage.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            | None -> ()

            use! response = client.SendAsync(requestMessage, CancellationToken.None)
            let! body = response.Content.ReadAsStringAsync()
            let output =
                if String.IsNullOrWhiteSpace body then
                    serialize {| statusCode = int response.StatusCode |}
                else
                    redactOutput body

            return int response.StatusCode, output
        }

    let private commandBaseUrl argv =
        collectGlobalOptions argv |> Result.map (fun options -> options.BaseUrl)

    let run argv =
        match buildPlan argv, commandBaseUrl argv with
        | Error err, _ ->
            Console.Error.WriteLine(err)
            2
        | _, Error err ->
            Console.Error.WriteLine(err)
            2
        | Ok plan, Ok baseUrl ->
            try
                use client = new HttpClient()

                match plan with
                | Single requestPlan ->
                    let statusCode, output = sendRequest client baseUrl requestPlan |> fun task -> task.GetAwaiter().GetResult()
                    Console.WriteLine(output)
                    if statusCode >= 200 && statusCode <= 299 then 0 else 1
                | Preflight(ready, opsSummary) ->
                    let readyStatus, readyOutput = sendRequest client baseUrl ready |> fun task -> task.GetAwaiter().GetResult()
                    let opsStatus, opsOutput = sendRequest client baseUrl opsSummary |> fun task -> task.GetAwaiter().GetResult()

                    let payload =
                        JsonObject(
                            [ KeyValuePair("readyStatusCode", JsonValue.Create(readyStatus) :> JsonNode)
                              KeyValuePair("opsSummaryStatusCode", JsonValue.Create(opsStatus) :> JsonNode)
                              KeyValuePair("ready", JsonNode.Parse(readyOutput))
                              KeyValuePair("opsSummary", JsonNode.Parse(opsOutput)) ]
                        )

                    Console.WriteLine(payload.ToJsonString(jsonOptions))

                    if readyStatus >= 200 && readyStatus <= 299 && opsStatus >= 200 && opsStatus <= 299 then 0 else 1
            with ex ->
                Console.Error.WriteLine(serialize {| error = "admin_cli_error"; message = redactOutput ex.Message |})
                1
