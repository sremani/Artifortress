open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Artifortress.Domain
open ObjectStorage
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Npgsql
open NpgsqlTypes

[<CLIMutable>]
type CreatePatRequest = {
    Subject: string
    Scopes: string array
    TtlMinutes: int
}

[<CLIMutable>]
type RevokePatRequest = {
    TokenId: Guid
}

[<CLIMutable>]
type CreateRepoRequest = {
    RepoKey: string
    RepoType: string
    UpstreamUrl: string
    MemberRepos: string array
}

[<CLIMutable>]
type UpsertRoleBindingRequest = {
    Roles: string array
}

[<CLIMutable>]
type CreateDraftVersionRequest = {
    PackageType: string
    PackageNamespace: string
    PackageName: string
    Version: string
}

[<CLIMutable>]
type CreateUploadSessionRequest = {
    ExpectedDigest: string
    ExpectedLength: int64
}

[<CLIMutable>]
type CreateUploadPartRequest = {
    PartNumber: int
}

[<CLIMutable>]
type UploadCompletedPartRequest = {
    PartNumber: int
    ETag: string
}

[<CLIMutable>]
type CompleteUploadPartsRequest = {
    Parts: UploadCompletedPartRequest array
}

[<CLIMutable>]
type AbortUploadRequest = {
    Reason: string
}

[<CLIMutable>]
type CommitUploadRequest = {
    // Reserved for future idempotency/proof fields.
    Noop: string
}

[<CLIMutable>]
type EvaluatePolicyRequest = {
    Action: string
    VersionId: Guid
    DecisionHint: string
    Reason: string
    PolicyEngineVersion: string
}

type PatRecord = {
    TokenId: Guid
    Subject: string
    TokenHash: string
    Scopes: RepoScope list
    ExpiresAtUtc: DateTimeOffset
    RevokedAtUtc: DateTimeOffset option
    CreatedAtUtc: DateTimeOffset
}

type RepoRecord = {
    RepoKey: string
    RepoType: string
    UpstreamUrl: string option
    MemberRepos: string list
    CreatedAtUtc: DateTimeOffset
    UpdatedAtUtc: DateTimeOffset
}

type RoleBindingRecord = {
    RepoKey: string
    Subject: string
    Roles: Set<RepoRole>
    UpdatedAtUtc: DateTimeOffset
}

type PackageVersionRecord = {
    VersionId: Guid
    RepoKey: string
    PackageType: string
    PackageNamespace: string option
    PackageName: string
    Version: string
    State: string
    CreatedAtUtc: DateTimeOffset
    PublishedAtUtc: DateTimeOffset option
}

type DraftVersionUpsertOutcome =
    | DraftCreated of PackageVersionRecord
    | DraftExisting of PackageVersionRecord
    | VersionStateConflict of string
    | RepoMissing

type UploadSessionRecord = {
    UploadId: Guid
    RepoKey: string
    ExpectedDigest: string
    ExpectedLength: int64
    State: string
    ObjectStagingKey: string option
    StorageUploadId: string option
    CommittedBlobDigest: string option
    ExpiresAtUtc: DateTimeOffset
    CreatedAtUtc: DateTimeOffset
}

type AuditRecord = {
    AuditId: int64
    TenantId: Guid
    Actor: string
    Action: string
    ResourceType: string
    ResourceId: string
    OccurredAtUtc: DateTimeOffset
    Details: Map<string, string>
}

type PolicyEvaluationResult = {
    EvaluationId: int64
    VersionId: Guid
    Action: string
    Decision: string
    DecisionSource: string
    Reason: string
    PolicyEngineVersion: string option
    EvaluatedAtUtc: DateTimeOffset
    QuarantineId: Guid option
}

type QuarantineItemRecord = {
    QuarantineId: Guid
    RepoKey: string
    VersionId: Guid
    Status: string
    Reason: string
    CreatedAtUtc: DateTimeOffset
    ResolvedAtUtc: DateTimeOffset option
    ResolvedBySubject: string option
}

type QuarantineResolveOutcome =
    | QuarantineResolved of QuarantineItemRecord
    | QuarantineAlreadyResolved of string
    | QuarantineMissing

type Principal = {
    TenantId: Guid
    TokenId: Guid
    Subject: string
    Scopes: RepoScope list
}

type AppState = {
    ConnectionString: string
    TenantSlug: string
    TenantName: string
    ObjectStorageClient: IObjectStorageClient
    PresignPartTtlSeconds: int
    PolicyEvaluationTimeoutMs: int
}

let nowUtc () = DateTimeOffset.UtcNow

let toUtcDateTimeOffset (value: DateTime) =
    if value.Kind = DateTimeKind.Utc then
        DateTimeOffset(value)
    elif value.Kind = DateTimeKind.Unspecified then
        DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
    else
        DateTimeOffset(value.ToUniversalTime())

let normalizeText (value: string) =
    if String.IsNullOrWhiteSpace value then "" else value.Trim()

let normalizeRepoKey (repoKey: string) = normalizeText repoKey |> fun value -> value.ToLowerInvariant()
let normalizeSubject (subject: string) = normalizeText subject |> fun value -> value.ToLowerInvariant()
let normalizePackageType (packageType: string) = normalizeText packageType |> fun value -> value.ToLowerInvariant()
let normalizePackageNamespace (packageNamespace: string) = normalizeText packageNamespace |> fun value -> value.ToLowerInvariant()
let normalizePackageName (packageName: string) = normalizeText packageName |> fun value -> value.ToLowerInvariant()

let private isHexChar (ch: char) =
    (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')

let normalizeDigest (digest: string) = normalizeText digest |> fun value -> value.ToLowerInvariant()

let validateUploadSessionRequest (request: CreateUploadSessionRequest) =
    let digest = normalizeDigest request.ExpectedDigest

    if digest.Length <> 64 || not (digest |> Seq.forall isHexChar) then
        Error "expectedDigest must be a 64-character lowercase hex SHA-256 digest."
    elif request.ExpectedLength <= 0L then
        Error "expectedLength must be greater than zero."
    else
        Ok(digest, request.ExpectedLength)

let validateUploadPartRequest (request: CreateUploadPartRequest) =
    if request.PartNumber < 1 then
        Error "partNumber must be greater than zero."
    else
        Ok request.PartNumber

let validateDraftVersionRequest (request: CreateDraftVersionRequest) =
    let packageType = normalizePackageType request.PackageType
    let packageNamespace =
        let value = normalizePackageNamespace request.PackageNamespace
        if String.IsNullOrWhiteSpace value then None else Some value

    let packageName = normalizePackageName request.PackageName
    let version = normalizeText request.Version

    if String.IsNullOrWhiteSpace packageType then
        Error "packageType is required."
    elif String.IsNullOrWhiteSpace packageName then
        Error "packageName is required."
    elif String.IsNullOrWhiteSpace version then
        Error "version is required."
    else
        Ok(packageType, packageNamespace, packageName, version)

let validateEvaluatePolicyRequest (request: EvaluatePolicyRequest) =
    let action = normalizeText request.Action |> fun value -> value.ToLowerInvariant()
    let versionId = request.VersionId
    let decisionHint = normalizeText request.DecisionHint |> fun value -> value.ToLowerInvariant()
    let reason = normalizeText request.Reason
    let policyEngineVersion =
        let value = normalizeText request.PolicyEngineVersion
        if String.IsNullOrWhiteSpace value then None else Some value

    let resolvedDecisionResult =
        if String.IsNullOrWhiteSpace decisionHint then
            Ok("allow", "default_allow")
        else
            match decisionHint with
            | "allow" -> Ok("allow", "hint_allow")
            | "deny" -> Ok("deny", "hint_deny")
            | "quarantine" -> Ok("quarantine", "hint_quarantine")
            | _ -> Error "decisionHint must be one of: allow, deny, quarantine."

    if action <> "publish" && action <> "promote" then
        Error "action must be one of: publish, promote."
    elif versionId = Guid.Empty then
        Error "versionId is required and must be a non-empty GUID."
    elif String.IsNullOrWhiteSpace reason then
        Error "reason is required."
    else
        resolvedDecisionResult
        |> Result.map (fun (decision, decisionSource) ->
            action, versionId, decision, decisionSource, reason, policyEngineVersion)

let validateQuarantineStatusFilter (statusValue: string) =
    let normalized = normalizeText statusValue |> fun value -> value.ToLowerInvariant()

    if String.IsNullOrWhiteSpace normalized then
        Ok None
    else
        match normalized with
        | "quarantined"
        | "released"
        | "rejected" -> Ok(Some normalized)
        | _ -> Error "status must be one of: quarantined, released, rejected."

let private normalizePartEtag (etag: string) =
    normalizeText etag |> fun value -> value.Trim('"')

let validateCompleteUploadRequest (request: CompleteUploadPartsRequest) =
    let values = if isNull request.Parts then [||] else request.Parts

    if values.Length = 0 then
        Error "At least one completed part is required."
    else
        let parsedResult =
            values
            |> Array.toList
            |> List.fold
                (fun acc value ->
                    match acc with
                    | Error err -> Error err
                    | Ok (parts: CompletedPart list) ->
                        let partNumber = value.PartNumber
                        let etag = normalizePartEtag value.ETag

                        if partNumber < 1 then
                            Error "partNumber must be greater than zero."
                        elif String.IsNullOrWhiteSpace etag then
                            Error "etag is required for each completed part."
                        elif parts |> List.exists (fun part -> part.PartNumber = partNumber) then
                            Error $"Duplicate partNumber '{partNumber}' is not allowed."
                        else
                            Ok(({ PartNumber = partNumber; ETag = etag }: CompletedPart) :: parts))
                (Ok ([]: CompletedPart list))

        parsedResult |> Result.map List.rev

let normalizeAbortReason (reason: string) =
    let value = normalizeText reason
    if String.IsNullOrWhiteSpace value then "client_abort" else value

let parseSingleRangeHeader (rawRangeHeader: string) =
    let headerValue = normalizeText rawRangeHeader

    if String.IsNullOrWhiteSpace headerValue then
        Ok None
    elif not (headerValue.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) then
        Error "Range header must use bytes=<start>-<end> format."
    else
        let spec = headerValue.Substring("bytes=".Length).Trim()

        if spec.Contains(",") then
            Error "Multiple byte ranges are not supported."
        else
            let parts = spec.Split('-', StringSplitOptions.None)

            if parts.Length <> 2 then
                Error "Range header must include a single '-' separator."
            else
                let startText = parts.[0].Trim()
                let endText = parts.[1].Trim()

                if String.IsNullOrWhiteSpace startText then
                    Error "Suffix byte ranges are not supported."
                else
                    match Int64.TryParse(startText) with
                    | false, _ -> Error "Range start must be a non-negative integer."
                    | true, startOffset when startOffset < 0L -> Error "Range start must be non-negative."
                    | true, startOffset ->
                        if String.IsNullOrWhiteSpace endText then
                            Ok(Some(startOffset, None))
                        else
                            match Int64.TryParse(endText) with
                            | false, _ -> Error "Range end must be a non-negative integer."
                            | true, endOffset when endOffset < startOffset ->
                                Error "Range end must be greater than or equal to range start."
                            | true, endOffset -> Ok(Some(startOffset, Some endOffset))

let tryResolvePolicyDecisionWithTimeout
    (timeoutMs: int)
    (decision: string)
    (decisionSource: string)
    (policyEngineVersion: string option)
    =
    let shouldSimulateTimeout =
        match policyEngineVersion with
        | Some value when String.Equals(normalizeText value, "simulate_timeout", StringComparison.OrdinalIgnoreCase) -> true
        | _ -> false

    let operation () =
        if shouldSimulateTimeout then
            Threading.Thread.Sleep(timeoutMs + 50)

        decision, decisionSource

    let task = Threading.Tasks.Task.Run(fun () -> operation ())

    if task.Wait(timeoutMs) then
        Ok task.Result
    else
        Error "Policy evaluation timed out."

let buildStagingObjectKey (tenantId: Guid) (repoKey: string) (uploadId: Guid) =
    $"staging/{tenantId:N}/{repoKey}/{uploadId:N}"

let computeSha256AndLength (stream: IO.Stream) =
    use hasher = SHA256.Create()
    let buffer = Array.zeroCreate<byte> 65536
    let mutable totalBytes = 0L
    let mutable bytesRead = stream.Read(buffer, 0, buffer.Length)

    while bytesRead > 0 do
        hasher.TransformBlock(buffer, 0, bytesRead, null, 0) |> ignore
        totalBytes <- totalBytes + int64 bytesRead
        bytesRead <- stream.Read(buffer, 0, buffer.Length)

    hasher.TransformFinalBlock(Array.empty<byte>, 0, 0) |> ignore

    let digest =
        if isNull hasher.Hash then
            ""
        else
            hasher.Hash |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    digest, totalBytes

let toTokenHash (rawToken: string) =
    use hasher = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(rawToken)
    hasher.ComputeHash(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let secureEquals (left: string) (right: string) =
    if String.IsNullOrEmpty left || String.IsNullOrEmpty right then
        false
    else
        let leftBytes = Encoding.UTF8.GetBytes(left)
        let rightBytes = Encoding.UTF8.GetBytes(right)

        if leftBytes.Length <> rightBytes.Length then
            false
        else
            CryptographicOperations.FixedTimeEquals(ReadOnlySpan(leftBytes), ReadOnlySpan(rightBytes))

let createPlainToken () =
    let bytes = RandomNumberGenerator.GetBytes(32)
    Convert.ToHexString(bytes).ToLowerInvariant()

let badRequest message =
    Results.Json({| error = "bad_request"; message = message |}, statusCode = StatusCodes.Status400BadRequest)

let unauthorized message =
    Results.Json({| error = "unauthorized"; message = message |}, statusCode = StatusCodes.Status401Unauthorized)

let forbidden message =
    Results.Json({| error = "forbidden"; message = message |}, statusCode = StatusCodes.Status403Forbidden)

let conflict message =
    Results.Json({| error = "conflict"; message = message |}, statusCode = StatusCodes.Status409Conflict)

let serviceUnavailable message =
    Results.Json(
        {| error = "service_unavailable"
           message = message |},
        statusCode = StatusCodes.Status503ServiceUnavailable
    )

let mapObjectStorageErrorToResult err =
    match err with
    | InvalidRequest message -> badRequest message
    | NotFound message ->
        Results.Json({| error = "object_not_found"; message = message |}, statusCode = StatusCodes.Status404NotFound)
    | InvalidRange message ->
        Results.Json({| error = "invalid_range"; message = message |}, statusCode = StatusCodes.Status416RequestedRangeNotSatisfiable)
    | AccessDenied message ->
        Results.Json({| error = "storage_access_denied"; message = message |}, statusCode = StatusCodes.Status503ServiceUnavailable)
    | TransientFailure message -> serviceUnavailable message
    | UnexpectedFailure message -> serviceUnavailable message

let tryReadBearerToken (ctx: HttpContext) =
    let header = ctx.Request.Headers.Authorization.ToString()

    if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
        let token = header.Substring("Bearer ".Length).Trim()
        if String.IsNullOrWhiteSpace token then None else Some token
    else
        None

let parseScopes (rawScopes: string array) =
    let values = if isNull rawScopes then [||] else rawScopes

    let parsed =
        values
        |> Array.toList
        |> List.map (fun scopeText -> RepoScope.tryParse scopeText)

    let errors =
        parsed
        |> List.choose (function
            | Error err -> Some err
            | Ok _ -> None)

    if errors.IsEmpty then
        Ok(
            parsed
            |> List.choose (function
                | Ok scope -> Some scope
                | Error _ -> None)
        )
    else
        Error(String.concat "; " errors)

let parseRoles (rawRoles: string array) =
    let values = if isNull rawRoles then [||] else rawRoles

    if values.Length = 0 then
        Error "At least one role is required."
    else
        let parsed =
            values
            |> Array.toList
            |> List.map (fun roleText -> RepoRole.tryParse roleText)

        let errors =
            parsed
            |> List.choose (function
                | Error err -> Some err
                | Ok _ -> None)

        if errors.IsEmpty then
            Ok(
                parsed
                |> List.choose (function
                    | Ok role -> Some role
                    | Error _ -> None)
                |> Set.ofList
            )
        else
            Error(String.concat "; " errors)

let readJsonBody<'T> (ctx: HttpContext) =
    try
        let payload = ctx.Request.ReadFromJsonAsync<'T>().GetAwaiter().GetResult()

        if obj.ReferenceEquals(payload, null) then
            Error "Request body is required."
        else
            Ok payload
    with ex ->
        Error $"Invalid JSON body: {ex.Message}"

let withConnection (state: AppState) (handler: NpgsqlConnection -> Result<'T, string>) : Result<'T, string> =
    try
        use conn = new NpgsqlConnection(state.ConnectionString)
        conn.Open()
        handler conn
    with ex ->
        Error $"Database operation failed: {ex.Message}"

let ensureTenantId (state: AppState) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
insert into tenants (slug, name)
values (@slug, @name)
on conflict (slug) do update set name = excluded.name
returning tenant_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("slug", state.TenantSlug) |> ignore
    cmd.Parameters.AddWithValue("name", state.TenantName) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Error "Failed to resolve tenant id."
    else
        Ok(scalar :?> Guid)

let hasAnyTokensForTenant (tenantId: Guid) (conn: NpgsqlConnection) =
    use cmd = new NpgsqlCommand("select exists(select 1 from personal_access_tokens where tenant_id = @tenant_id);", conn)
    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as existsValue -> Ok existsValue
    | _ -> Error "Could not determine token existence."

let tryReadPrincipalByTokenHash (tokenHash: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select token_id, tenant_id, subject, scopes
from personal_access_tokens
where token_hash = @token_hash
  and revoked_at is null
  and expires_at > now()
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("token_hash", tokenHash) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let tokenId = reader.GetGuid(0)
        let tenantId = reader.GetGuid(1)
        let subject = reader.GetString(2)
        let rawScopes = reader.GetFieldValue<string[]>(3)

        match parseScopes rawScopes with
        | Ok scopes ->
            Ok(
                Some
                    { TenantId = tenantId
                      TokenId = tokenId
                      Subject = subject
                      Scopes = scopes }
            )
        | Error err -> Error $"Persisted scopes are invalid: {err}"
    else
        Ok None

let insertPat
    (tenantId: Guid)
    (subject: string)
    (tokenId: Guid)
    (tokenHash: string)
    (scopes: RepoScope list)
    (expiresAtUtc: DateTimeOffset)
    (createdBy: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into personal_access_tokens
  (token_id, tenant_id, subject, token_hash, scopes, expires_at, created_by_subject)
values
  (@token_id, @tenant_id, @subject, @token_hash, @scopes, @expires_at, @created_by_subject);
""",
            conn
        )

    let scopeValues = scopes |> List.map RepoScope.value |> List.toArray
    cmd.Parameters.AddWithValue("token_id", tokenId) |> ignore
    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("subject", subject) |> ignore
    cmd.Parameters.AddWithValue("token_hash", tokenHash) |> ignore

    let scopesParam = cmd.Parameters.Add("scopes", NpgsqlDbType.Array ||| NpgsqlDbType.Text)
    scopesParam.Value <- scopeValues

    cmd.Parameters.AddWithValue("expires_at", expiresAtUtc) |> ignore
    cmd.Parameters.AddWithValue("created_by_subject", createdBy) |> ignore

    let rows = cmd.ExecuteNonQuery()
    if rows = 1 then Ok() else Error "Token insert did not affect expected rows."

let revokePat (tenantId: Guid) (tokenId: Guid) (revokedBy: string) (conn: NpgsqlConnection) =
    use tx = conn.BeginTransaction()

    use updateCmd =
        new NpgsqlCommand(
            """
update personal_access_tokens
set revoked_at = now()
where tenant_id = @tenant_id
  and token_id = @token_id
  and revoked_at is null
returning token_id;
""",
            conn,
            tx
        )

    updateCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    updateCmd.Parameters.AddWithValue("token_id", tokenId) |> ignore

    let updatedToken = updateCmd.ExecuteScalar()

    if isNull updatedToken || updatedToken = box DBNull.Value then
        tx.Rollback()
        Ok false
    else
        use revocationCmd =
            new NpgsqlCommand(
                """
insert into token_revocations (tenant_id, token_id, revoked_by_subject)
values (@tenant_id, @token_id, @revoked_by_subject);
""",
                conn,
                tx
            )

        revocationCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        revocationCmd.Parameters.AddWithValue("token_id", tokenId) |> ignore
        revocationCmd.Parameters.AddWithValue("revoked_by_subject", revokedBy) |> ignore
        revocationCmd.ExecuteNonQuery() |> ignore

        tx.Commit()
        Ok true

let serializeRepoConfig (repo: RepoRecord) =
    match repo.RepoType with
    | "remote" ->
        JsonSerializer.Serialize(
            {| upstreamUrl =
                match repo.UpstreamUrl with
                | Some value -> value
                | None -> "" |}
        )
    | "virtual" -> JsonSerializer.Serialize({| memberRepos = repo.MemberRepos |> List.toArray |})
    | _ -> "{}"

let tryReadRepoConfig (repoType: string) (configJson: string) =
    try
        let jsonText = if String.IsNullOrWhiteSpace configJson then "{}" else configJson
        use doc = JsonDocument.Parse(jsonText)
        let root = doc.RootElement

        match repoType with
        | "remote" ->
            let mutable upstreamProp = Unchecked.defaultof<JsonElement>

            if root.TryGetProperty("upstreamUrl", &upstreamProp) then
                let upstream = upstreamProp.GetString()
                if String.IsNullOrWhiteSpace upstream then Ok(None, [])
                else Ok(Some upstream, [])
            else
                Ok(None, [])
        | "virtual" ->
            let mutable membersProp = Unchecked.defaultof<JsonElement>

            if root.TryGetProperty("memberRepos", &membersProp) && membersProp.ValueKind = JsonValueKind.Array then
                let members =
                    membersProp.EnumerateArray()
                    |> Seq.choose (fun item ->
                        let value = item.GetString()
                        if String.IsNullOrWhiteSpace value then None else Some(normalizeRepoKey value))
                    |> Seq.distinct
                    |> Seq.toList

                Ok(None, members)
            else
                Ok(None, [])
        | _ -> Ok(None, [])
    with ex ->
        Error $"Invalid repository config json: {ex.Message}"

let readReposForTenant (tenantId: Guid) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select repo_key, repo_type, config::text, created_at
from repos
where tenant_id = @tenant_id
order by repo_key;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore

    use reader = cmd.ExecuteReader()

    let rec loop acc =
        if reader.Read() then
            let repoKey = reader.GetString(0)
            let repoType = reader.GetString(1)
            let configJson = if reader.IsDBNull(2) then "{}" else reader.GetString(2)
            let createdAtUtc = reader.GetFieldValue<DateTimeOffset>(3)

            match tryReadRepoConfig repoType configJson with
            | Error err -> Error err
            | Ok(upstreamUrl, memberRepos) ->
                let repo =
                    { RepoKey = repoKey
                      RepoType = repoType
                      UpstreamUrl = upstreamUrl
                      MemberRepos = memberRepos
                      CreatedAtUtc = createdAtUtc
                      UpdatedAtUtc = createdAtUtc }

                loop (repo :: acc)
        else
            Ok(List.rev acc)

    loop []

let tryReadRepoByKey (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select repo_key, repo_type, config::text, created_at
from repos
where tenant_id = @tenant_id
  and repo_key = @repo_key
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let persistedRepoKey = reader.GetString(0)
        let repoType = reader.GetString(1)
        let configJson = if reader.IsDBNull(2) then "{}" else reader.GetString(2)
        let createdAtUtc = reader.GetFieldValue<DateTimeOffset>(3)

        match tryReadRepoConfig repoType configJson with
        | Error err -> Error err
        | Ok(upstreamUrl, memberRepos) ->
            Ok(
                Some
                    { RepoKey = persistedRepoKey
                      RepoType = repoType
                      UpstreamUrl = upstreamUrl
                      MemberRepos = memberRepos
                      CreatedAtUtc = createdAtUtc
                      UpdatedAtUtc = createdAtUtc }
            )
    else
        Ok None

let insertRepoForTenant (tenantId: Guid) (repo: RepoRecord) (conn: NpgsqlConnection) =
    try
        use cmd =
            new NpgsqlCommand(
                """
insert into repos (tenant_id, repo_key, repo_type, config)
values (@tenant_id, @repo_key, @repo_type, @config);
""",
                conn
            )

        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        cmd.Parameters.AddWithValue("repo_key", repo.RepoKey) |> ignore
        cmd.Parameters.AddWithValue("repo_type", repo.RepoType) |> ignore

        let configParam = cmd.Parameters.Add("config", NpgsqlDbType.Jsonb)
        configParam.Value <- serializeRepoConfig repo

        let rows = cmd.ExecuteNonQuery()
        Ok(rows = 1)
    with :? PostgresException as ex when ex.SqlState = "23505" ->
        Ok false

let repoExistsForTenant (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select exists(
  select 1
  from repos
  where tenant_id = @tenant_id
    and repo_key = @repo_key
);
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as existsValue -> Ok existsValue
    | _ -> Error "Could not determine repository existence."

let tryReadRepoIdForTenant (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select repo_id
from repos
where tenant_id = @tenant_id
  and repo_key = @repo_key
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        match scalar with
        | :? Guid as repoId -> Ok(Some repoId)
        | _ -> Error "Unexpected repository id type returned from database."

let upsertPackageForRepo
    (tenantId: Guid)
    (repoId: Guid)
    (packageType: string)
    (packageNamespace: string option)
    (packageName: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into packages (tenant_id, repo_id, package_type, namespace, name)
values (@tenant_id, @repo_id, @package_type, @namespace, @name)
on conflict (repo_id, package_type, coalesce(namespace, ''), name)
do update set name = excluded.name
returning package_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
    cmd.Parameters.AddWithValue("package_type", packageType) |> ignore

    let namespaceParam = cmd.Parameters.Add("namespace", NpgsqlDbType.Text)
    namespaceParam.Value <- (match packageNamespace with | Some value -> box value | None -> box DBNull.Value)

    cmd.Parameters.AddWithValue("name", packageName) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Error "Package upsert did not return package id."
    else
        match scalar with
        | :? Guid as packageId -> Ok packageId
        | _ -> Error "Unexpected package id type returned from database."

let tryInsertDraftPackageVersion
    (tenantId: Guid)
    (repoId: Guid)
    (packageId: Guid)
    (version: string)
    (createdBy: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into package_versions (tenant_id, repo_id, package_id, version, state, created_by_subject)
values (@tenant_id, @repo_id, @package_id, @version, 'draft', @created_by_subject)
on conflict (repo_id, package_id, version) do nothing
returning version_id, state, created_at, published_at;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
    cmd.Parameters.AddWithValue("package_id", packageId) |> ignore
    cmd.Parameters.AddWithValue("version", version) |> ignore
    cmd.Parameters.AddWithValue("created_by_subject", createdBy) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let versionId = reader.GetGuid(0)
        let stateValue = reader.GetString(1)
        let createdAt = reader.GetFieldValue<DateTime>(2) |> toUtcDateTimeOffset
        let publishedAt =
            if reader.IsDBNull(3) then None else Some(reader.GetFieldValue<DateTime>(3) |> toUtcDateTimeOffset)

        Ok(Some(versionId, stateValue, createdAt, publishedAt))
    else
        Ok None

let tryReadPackageVersionForRepo
    (repoId: Guid)
    (packageId: Guid)
    (version: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
select version_id, state, created_at, published_at
from package_versions
where repo_id = @repo_id
  and package_id = @package_id
  and version = @version
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
    cmd.Parameters.AddWithValue("package_id", packageId) |> ignore
    cmd.Parameters.AddWithValue("version", version) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let versionId = reader.GetGuid(0)
        let stateValue = reader.GetString(1)
        let createdAt = reader.GetFieldValue<DateTime>(2) |> toUtcDateTimeOffset
        let publishedAt =
            if reader.IsDBNull(3) then None else Some(reader.GetFieldValue<DateTime>(3) |> toUtcDateTimeOffset)

        Ok(Some(versionId, stateValue, createdAt, publishedAt))
    else
        Ok None

let createOrGetDraftVersionForRepo
    (tenantId: Guid)
    (repoKey: string)
    (packageType: string)
    (packageNamespace: string option)
    (packageName: string)
    (version: string)
    (createdBy: string)
    (conn: NpgsqlConnection)
    =
    let toRecord (versionId, stateValue, createdAtUtc, publishedAtUtc) =
        { VersionId = versionId
          RepoKey = repoKey
          PackageType = packageType
          PackageNamespace = packageNamespace
          PackageName = packageName
          Version = version
          State = stateValue
          CreatedAtUtc = createdAtUtc
          PublishedAtUtc = publishedAtUtc }

    tryReadRepoIdForTenant tenantId repoKey conn
    |> Result.bind (fun repoIdOption ->
        match repoIdOption with
        | None -> Ok RepoMissing
        | Some repoId ->
            upsertPackageForRepo tenantId repoId packageType packageNamespace packageName conn
            |> Result.bind (fun packageId ->
                tryInsertDraftPackageVersion tenantId repoId packageId version createdBy conn
                |> Result.bind (fun insertResult ->
                    match insertResult with
                    | Some insertedVersion ->
                        insertedVersion |> toRecord |> DraftCreated |> Ok
                    | None ->
                        tryReadPackageVersionForRepo repoId packageId version conn
                        |> Result.bind (fun existing ->
                            match existing with
                            | None -> Error "Could not resolve package version after upsert."
                            | Some existingVersion ->
                                let record = toRecord existingVersion
                                if String.Equals(record.State, "draft", StringComparison.Ordinal) then
                                    Ok(DraftExisting record)
                                else
                                    Ok(VersionStateConflict record.State)))))

let evaluatePolicyForVersion
    (tenantId: Guid)
    (repoKey: string)
    (versionId: Guid)
    (action: string)
    (decision: string)
    (decisionSource: string)
    (reason: string)
    (policyEngineVersion: string option)
    (evaluatedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use tx = conn.BeginTransaction()

    use targetCmd =
        new NpgsqlCommand(
            """
select r.repo_id, pv.state
from repos r
join package_versions pv on pv.repo_id = r.repo_id and pv.tenant_id = r.tenant_id
where r.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and pv.version_id = @version_id
limit 1;
""",
            conn,
            tx
        )

    targetCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    targetCmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    targetCmd.Parameters.AddWithValue("version_id", versionId) |> ignore

    use targetReader = targetCmd.ExecuteReader()

    let target =
        if targetReader.Read() then
            let repoId = targetReader.GetGuid(0)
            let versionState = targetReader.GetString(1)
            Some(repoId, versionState)
        else
            None

    targetReader.Close()

    match target with
    | None ->
        tx.Rollback()
        Ok None
    | Some(repoId, versionState) ->
        let details =
            [ "decisionSource", decisionSource; "versionState", versionState ]
            |> Map.ofList
            |> Map.toSeq
            |> dict
            |> JsonSerializer.Serialize

        use insertEvalCmd =
            new NpgsqlCommand(
                """
insert into policy_evaluations
  (tenant_id, repo_id, version_id, action, decision, policy_engine_version, reason, details, evaluated_by_subject)
values
  (@tenant_id, @repo_id, @version_id, @action, @decision, @policy_engine_version, @reason, @details, @evaluated_by_subject)
returning evaluation_id, evaluated_at;
""",
                conn,
                tx
            )

        insertEvalCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        insertEvalCmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
        insertEvalCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        insertEvalCmd.Parameters.AddWithValue("action", action) |> ignore
        insertEvalCmd.Parameters.AddWithValue("decision", decision) |> ignore
        insertEvalCmd.Parameters.AddWithValue("reason", reason) |> ignore
        insertEvalCmd.Parameters.AddWithValue("evaluated_by_subject", evaluatedBySubject) |> ignore

        let policyEngineParam = insertEvalCmd.Parameters.Add("policy_engine_version", NpgsqlDbType.Text)
        policyEngineParam.Value <- (match policyEngineVersion with | Some value -> box value | None -> box DBNull.Value)

        let detailsParam = insertEvalCmd.Parameters.Add("details", NpgsqlDbType.Jsonb)
        detailsParam.Value <- details

        use evalReader = insertEvalCmd.ExecuteReader()

        let evaluation =
            if evalReader.Read() then
                let evaluationId = evalReader.GetInt64(0)
                let evaluatedAtUtc = evalReader.GetFieldValue<DateTime>(1) |> toUtcDateTimeOffset
                Some(evaluationId, evaluatedAtUtc)
            else
                None

        evalReader.Close()

        match evaluation with
        | None ->
            tx.Rollback()
            Error "Policy evaluation insert did not return evaluation metadata."
        | Some(evaluationId, evaluatedAtUtc) ->
            let quarantineIdResult =
                if decision <> "quarantine" then
                    Ok None
                else
                    use quarantineCmd =
                        new NpgsqlCommand(
                            """
insert into quarantine_items
  (tenant_id, repo_id, version_id, status, reason)
values
  (@tenant_id, @repo_id, @version_id, 'quarantined', @reason)
on conflict (tenant_id, repo_id, version_id)
do update set
  status = 'quarantined',
  reason = excluded.reason,
  resolved_at = null,
  resolved_by_subject = null
returning quarantine_id;
""",
                            conn,
                            tx
                        )

                    quarantineCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                    quarantineCmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
                    quarantineCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
                    quarantineCmd.Parameters.AddWithValue("reason", reason) |> ignore

                    let scalar = quarantineCmd.ExecuteScalar()

                    if isNull scalar || scalar = box DBNull.Value then
                        Error "Quarantine upsert did not return quarantine id."
                    else
                        match scalar with
                        | :? Guid as quarantineId -> Ok(Some quarantineId)
                        | _ -> Error "Unexpected quarantine id type returned from database."

            match quarantineIdResult with
            | Error err ->
                tx.Rollback()
                Error err
            | Ok quarantineId ->
                tx.Commit()

                Ok(
                    Some
                        { EvaluationId = evaluationId
                          VersionId = versionId
                          Action = action
                          Decision = decision
                          DecisionSource = decisionSource
                          Reason = reason
                          PolicyEngineVersion = policyEngineVersion
                          EvaluatedAtUtc = evaluatedAtUtc
                          QuarantineId = quarantineId }
                )

let readQuarantineItemsForRepo
    (tenantId: Guid)
    (repoKey: string)
    (statusFilter: string option)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
select qi.quarantine_id, qi.version_id, qi.status, qi.reason, qi.created_at, qi.resolved_at, qi.resolved_by_subject
from quarantine_items qi
join repos r on r.repo_id = qi.repo_id
where qi.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and (@status_filter is null or qi.status = @status_filter)
order by qi.created_at desc;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    let statusParam = cmd.Parameters.Add("status_filter", NpgsqlDbType.Text)
    statusParam.Value <- (match statusFilter with | Some value -> box value | None -> box DBNull.Value)

    use reader = cmd.ExecuteReader()
    let entries = ResizeArray<QuarantineItemRecord>()

    let rec loop () =
        if reader.Read() then
            let quarantineId = reader.GetGuid(0)
            let versionId = reader.GetGuid(1)
            let status = reader.GetString(2)
            let reason = reader.GetString(3)
            let createdAtUtc = reader.GetFieldValue<DateTime>(4) |> toUtcDateTimeOffset
            let resolvedAtUtc =
                if reader.IsDBNull(5) then None else Some(reader.GetFieldValue<DateTime>(5) |> toUtcDateTimeOffset)

            let resolvedBySubject =
                if reader.IsDBNull(6) then None else Some(reader.GetString(6))

            entries.Add(
                { QuarantineId = quarantineId
                  RepoKey = repoKey
                  VersionId = versionId
                  Status = status
                  Reason = reason
                  CreatedAtUtc = createdAtUtc
                  ResolvedAtUtc = resolvedAtUtc
                  ResolvedBySubject = resolvedBySubject }
            )

            loop ()
        else
            Ok(entries |> Seq.toList)

    loop ()

let tryReadQuarantineItemForRepo (tenantId: Guid) (repoKey: string) (quarantineId: Guid) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select qi.quarantine_id, qi.version_id, qi.status, qi.reason, qi.created_at, qi.resolved_at, qi.resolved_by_subject
from quarantine_items qi
join repos r on r.repo_id = qi.repo_id
where qi.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and qi.quarantine_id = @quarantine_id
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("quarantine_id", quarantineId) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let versionId = reader.GetGuid(1)
        let status = reader.GetString(2)
        let reason = reader.GetString(3)
        let createdAtUtc = reader.GetFieldValue<DateTime>(4) |> toUtcDateTimeOffset
        let resolvedAtUtc =
            if reader.IsDBNull(5) then None else Some(reader.GetFieldValue<DateTime>(5) |> toUtcDateTimeOffset)

        let resolvedBySubject =
            if reader.IsDBNull(6) then None else Some(reader.GetString(6))

        Ok(
            Some
                { QuarantineId = quarantineId
                  RepoKey = repoKey
                  VersionId = versionId
                  Status = status
                  Reason = reason
                  CreatedAtUtc = createdAtUtc
                  ResolvedAtUtc = resolvedAtUtc
                  ResolvedBySubject = resolvedBySubject }
        )
    else
        Ok None

let resolveQuarantineItemForRepo
    (tenantId: Guid)
    (repoKey: string)
    (quarantineId: Guid)
    (targetStatus: string)
    (resolvedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use updateCmd =
        new NpgsqlCommand(
            """
update quarantine_items qi
set status = @target_status,
    resolved_at = now(),
    resolved_by_subject = @resolved_by_subject
from repos r
where qi.tenant_id = @tenant_id
  and r.tenant_id = qi.tenant_id
  and r.repo_id = qi.repo_id
  and r.repo_key = @repo_key
  and qi.quarantine_id = @quarantine_id
  and qi.status = 'quarantined'
returning qi.version_id, qi.status, qi.reason, qi.created_at, qi.resolved_at, qi.resolved_by_subject;
""",
            conn
        )

    updateCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    updateCmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    updateCmd.Parameters.AddWithValue("quarantine_id", quarantineId) |> ignore
    updateCmd.Parameters.AddWithValue("target_status", targetStatus) |> ignore
    updateCmd.Parameters.AddWithValue("resolved_by_subject", resolvedBySubject) |> ignore

    use updateReader = updateCmd.ExecuteReader()

    if updateReader.Read() then
        let versionId = updateReader.GetGuid(0)
        let status = updateReader.GetString(1)
        let reason = updateReader.GetString(2)
        let createdAtUtc = updateReader.GetFieldValue<DateTime>(3) |> toUtcDateTimeOffset
        let resolvedAtUtc =
            if updateReader.IsDBNull(4) then None else Some(updateReader.GetFieldValue<DateTime>(4) |> toUtcDateTimeOffset)

        let resolvedByValue =
            if updateReader.IsDBNull(5) then None else Some(updateReader.GetString(5))

        Ok(
            QuarantineResolved
                { QuarantineId = quarantineId
                  RepoKey = repoKey
                  VersionId = versionId
                  Status = status
                  Reason = reason
                  CreatedAtUtc = createdAtUtc
                  ResolvedAtUtc = resolvedAtUtc
                  ResolvedBySubject = resolvedByValue }
        )
    else
        updateReader.Close()

        tryReadQuarantineItemForRepo tenantId repoKey quarantineId conn
        |> Result.map (function
            | Some item -> QuarantineAlreadyResolved item.Status
            | None -> QuarantineMissing)

let deleteRepoForTenant (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
delete from repos
where tenant_id = @tenant_id
  and repo_key = @repo_key;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    let rows = cmd.ExecuteNonQuery()
    Ok(rows > 0)

let tryReadBlobLengthByDigest (digest: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select length_bytes
from blobs
where digest = @digest
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        match scalar with
        | :? int64 as lengthValue -> Ok(Some lengthValue)
        | :? int32 as lengthValue -> Ok(Some(int64 lengthValue))
        | _ -> Error "Unexpected blob length type returned from database."

let tryReadBlobStorageKeyByDigest (digest: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select storage_key
from blobs
where digest = @digest
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        match scalar with
        | :? string as storageKey -> Ok(Some storageKey)
        | _ -> Error "Unexpected blob storage key type returned from database."

let repoHasCommittedBlobDigest (tenantId: Guid) (repoKey: string) (digest: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select exists(
  select 1
  from upload_sessions us
  join repos r on r.repo_id = us.repo_id
  where us.tenant_id = @tenant_id
    and r.repo_key = @repo_key
    and us.state = 'committed'
    and us.committed_blob_digest = @digest
);
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as existsValue -> Ok existsValue
    | _ -> Error "Could not determine committed blob visibility for repository."

let repoBlobDigestBlockedByQuarantine (tenantId: Guid) (repoKey: string) (digest: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select exists(
  select 1
  from artifact_entries ae
  join package_versions pv on pv.version_id = ae.version_id
  join repos r on r.repo_id = pv.repo_id and r.tenant_id = pv.tenant_id
  join quarantine_items qi
    on qi.tenant_id = pv.tenant_id
   and qi.repo_id = pv.repo_id
   and qi.version_id = pv.version_id
  where pv.tenant_id = @tenant_id
    and r.repo_key = @repo_key
    and ae.blob_digest = @digest
    and qi.status in ('quarantined', 'rejected')
);
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as blocked -> Ok blocked
    | _ -> Error "Could not determine quarantine visibility for repository blob."

let insertUploadSessionForRepo
    (tenantId: Guid)
    (repoKey: string)
    (expectedDigest: string)
    (expectedLength: int64)
    (stateValue: string)
    (objectStagingKey: string option)
    (storageUploadId: string option)
    (committedBlobDigest: string option)
    (createdBySubject: string)
    (expiresAtUtc: DateTimeOffset)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
with target_repo as (
  select repo_id
  from repos
  where tenant_id = @tenant_id
    and repo_key = @repo_key
)
insert into upload_sessions
  (tenant_id, repo_id, expected_digest, expected_length, state, object_staging_key, storage_upload_id, committed_blob_digest, created_by_subject, expires_at, committed_at)
select
  @tenant_id,
  target_repo.repo_id,
  @expected_digest,
  @expected_length,
  @state,
  @object_staging_key,
  @storage_upload_id,
  @committed_blob_digest,
  @created_by_subject,
  @expires_at,
  case when @state = 'committed' then now() else null end
from target_repo
returning upload_id, expected_digest, expected_length, state, object_staging_key, storage_upload_id, committed_blob_digest, expires_at, created_at;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("expected_digest", expectedDigest) |> ignore
    cmd.Parameters.AddWithValue("expected_length", expectedLength) |> ignore
    cmd.Parameters.AddWithValue("state", stateValue) |> ignore

    let objectStagingParam = cmd.Parameters.Add("object_staging_key", NpgsqlDbType.Text)
    objectStagingParam.Value <- (match objectStagingKey with | Some value -> box value | None -> box DBNull.Value)

    let storageUploadParam = cmd.Parameters.Add("storage_upload_id", NpgsqlDbType.Text)
    storageUploadParam.Value <- (match storageUploadId with | Some value -> box value | None -> box DBNull.Value)

    let committedBlobParam = cmd.Parameters.Add("committed_blob_digest", NpgsqlDbType.Char)
    committedBlobParam.Value <- (match committedBlobDigest with | Some value -> box value | None -> box DBNull.Value)

    cmd.Parameters.AddWithValue("created_by_subject", createdBySubject) |> ignore
    cmd.Parameters.AddWithValue("expires_at", expiresAtUtc) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let uploadId = reader.GetGuid(0)
        let returnedDigest = reader.GetString(1)
        let returnedLength = reader.GetInt64(2)
        let returnedState = reader.GetString(3)
        let returnedObjectStagingKey = if reader.IsDBNull(4) then None else Some(reader.GetString(4))
        let returnedStorageUploadId = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
        let returnedCommittedBlobDigest = if reader.IsDBNull(6) then None else Some(reader.GetString(6))
        let expiresAt = reader.GetFieldValue<DateTimeOffset>(7)
        let createdAt = reader.GetFieldValue<DateTimeOffset>(8)

        Ok(
            Some
                { UploadId = uploadId
                  RepoKey = repoKey
                  ExpectedDigest = returnedDigest
                  ExpectedLength = returnedLength
                  State = returnedState
                  ObjectStagingKey = returnedObjectStagingKey
                  StorageUploadId = returnedStorageUploadId
                  CommittedBlobDigest = returnedCommittedBlobDigest
                  ExpiresAtUtc = expiresAt
                  CreatedAtUtc = createdAt }
        )
    else
        Ok None

let tryReadUploadSessionForRepo (tenantId: Guid) (repoKey: string) (uploadId: Guid) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select us.upload_id,
       us.expected_digest,
       us.expected_length,
       us.state,
       us.object_staging_key,
       us.storage_upload_id,
       us.committed_blob_digest,
       us.expires_at,
       us.created_at
from upload_sessions us
join repos r on r.repo_id = us.repo_id
where us.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and us.upload_id = @upload_id
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let expectedDigest = reader.GetString(1)
        let expectedLength = reader.GetInt64(2)
        let stateValue = reader.GetString(3)
        let objectStagingKey = if reader.IsDBNull(4) then None else Some(reader.GetString(4))
        let storageUploadId = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
        let committedBlobDigest = if reader.IsDBNull(6) then None else Some(reader.GetString(6))
        let expiresAtUtc = reader.GetFieldValue<DateTimeOffset>(7)
        let createdAtUtc = reader.GetFieldValue<DateTimeOffset>(8)

        Ok(
            Some
                { UploadId = uploadId
                  RepoKey = repoKey
                  ExpectedDigest = expectedDigest
                  ExpectedLength = expectedLength
                  State = stateValue
                  ObjectStagingKey = objectStagingKey
                  StorageUploadId = storageUploadId
                  CommittedBlobDigest = committedBlobDigest
                  ExpiresAtUtc = expiresAtUtc
                  CreatedAtUtc = createdAtUtc }
        )
    else
        Ok None

let updateUploadSessionState (tenantId: Guid) (uploadId: Guid) (expectedCurrentState: string) (nextState: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
update upload_sessions
set state = @next_state,
    updated_at = now()
where tenant_id = @tenant_id
  and upload_id = @upload_id
  and state = @expected_current_state
returning upload_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore
    cmd.Parameters.AddWithValue("expected_current_state", expectedCurrentState) |> ignore
    cmd.Parameters.AddWithValue("next_state", nextState) |> ignore

    let scalar = cmd.ExecuteScalar()
    Ok(not (isNull scalar || scalar = box DBNull.Value))

let markUploadSessionAborted (tenantId: Guid) (uploadId: Guid) (reason: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
update upload_sessions
set state = 'aborted',
    aborted_at = now(),
    aborted_reason = @reason,
    updated_at = now()
where tenant_id = @tenant_id
  and upload_id = @upload_id
  and state in ('initiated', 'parts_uploading', 'pending_commit')
returning upload_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore
    cmd.Parameters.AddWithValue("reason", reason) |> ignore

    let scalar = cmd.ExecuteScalar()
    Ok(not (isNull scalar || scalar = box DBNull.Value))

let commitUploadSessionWithBlob
    (tenantId: Guid)
    (uploadId: Guid)
    (expectedDigest: string)
    (expectedLength: int64)
    (storageKey: string)
    (objectEtag: string option)
    (conn: NpgsqlConnection)
    =
    use tx = conn.BeginTransaction()

    use upsertBlobCmd =
        new NpgsqlCommand(
            """
insert into blobs (digest, length_bytes, storage_key, object_etag)
values (@digest, @length_bytes, @storage_key, @object_etag)
on conflict (digest)
do update set
  object_etag = coalesce(blobs.object_etag, excluded.object_etag)
returning length_bytes;
""",
            conn,
            tx
        )

    upsertBlobCmd.Parameters.AddWithValue("digest", expectedDigest) |> ignore
    upsertBlobCmd.Parameters.AddWithValue("length_bytes", expectedLength) |> ignore
    upsertBlobCmd.Parameters.AddWithValue("storage_key", storageKey) |> ignore

    let objectEtagParam = upsertBlobCmd.Parameters.Add("object_etag", NpgsqlDbType.Text)
    objectEtagParam.Value <- (match objectEtag with | Some value -> box value | None -> box DBNull.Value)

    let lengthScalar = upsertBlobCmd.ExecuteScalar()

    let persistedLengthResult =
        if isNull lengthScalar || lengthScalar = box DBNull.Value then
            Error "Blob upsert did not return persisted length."
        else
            match lengthScalar with
            | :? int64 as value -> Ok value
            | :? int32 as value -> Ok(int64 value)
            | _ -> Error "Unexpected persisted blob length type."

    match persistedLengthResult with
    | Error err ->
        tx.Rollback()
        Error err
    | Ok persistedLength when persistedLength <> expectedLength ->
        tx.Rollback()
        Error "Digest already exists with a different length."
    | Ok _ ->
        use updateSessionCmd =
            new NpgsqlCommand(
                """
update upload_sessions
set state = 'committed',
    committed_blob_digest = @digest,
    committed_at = now(),
    updated_at = now()
where tenant_id = @tenant_id
  and upload_id = @upload_id
  and state = 'pending_commit'
returning upload_id;
""",
                conn,
                tx
            )

        updateSessionCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        updateSessionCmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore
        updateSessionCmd.Parameters.AddWithValue("digest", expectedDigest) |> ignore

        let updateScalar = updateSessionCmd.ExecuteScalar()
        let updated = not (isNull updateScalar || updateScalar = box DBNull.Value)

        if updated then
            tx.Commit()
            Ok true
        else
            tx.Rollback()
            Ok false

let tryAuthenticate (state: AppState) (ctx: HttpContext) =
    match tryReadBearerToken ctx with
    | None -> Ok None
    | Some rawToken ->
        let tokenHash = toTokenHash rawToken
        withConnection state (tryReadPrincipalByTokenHash tokenHash)

let requireRole (state: AppState) (ctx: HttpContext) (repoKey: string) (requiredRole: RepoRole) =
    match tryAuthenticate state ctx with
    | Error err -> Error(serviceUnavailable err)
    | Ok None -> Error(unauthorized "Missing or invalid bearer token.")
    | Ok(Some principal) when Authorization.hasRole principal.Scopes repoKey requiredRole -> Ok principal
    | Ok(Some _) -> Error(forbidden "Caller does not have the required repository role.")

let serializeAuditDetails (details: Map<string, string>) =
    details |> Map.toSeq |> dict |> JsonSerializer.Serialize

let tryParseAuditDetails (jsonText: string) =
    try
        let normalized = if String.IsNullOrWhiteSpace jsonText then "{}" else jsonText
        use doc = JsonDocument.Parse(normalized)
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error "Audit details json must be an object."
        else
            root.EnumerateObject()
            |> Seq.map (fun prop ->
                let value =
                    match prop.Value.ValueKind with
                    | JsonValueKind.String -> prop.Value.GetString()
                    | JsonValueKind.True
                    | JsonValueKind.False
                    | JsonValueKind.Number
                    | JsonValueKind.Array
                    | JsonValueKind.Object
                    | JsonValueKind.Null -> prop.Value.GetRawText()
                    | _ -> prop.Value.ToString()

                prop.Name, value)
            |> Map.ofSeq
            |> Ok
    with ex ->
        Error $"Invalid audit details json: {ex.Message}"

let insertAuditRecord
    (tenantId: Guid)
    (actor: string)
    (action: string)
    (resourceType: string)
    (resourceId: string)
    (details: Map<string, string>)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into audit_log (tenant_id, actor_subject, action, resource_type, resource_id, details)
values (@tenant_id, @actor_subject, @action, @resource_type, @resource_id, @details);
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("actor_subject", actor) |> ignore
    cmd.Parameters.AddWithValue("action", action) |> ignore
    cmd.Parameters.AddWithValue("resource_type", resourceType) |> ignore
    cmd.Parameters.AddWithValue("resource_id", resourceId) |> ignore

    let detailsParam = cmd.Parameters.Add("details", NpgsqlDbType.Jsonb)
    detailsParam.Value <- serializeAuditDetails details

    let rows = cmd.ExecuteNonQuery()
    if rows = 1 then Ok() else Error "Audit insert did not affect expected rows."

let writeAudit
    (state: AppState)
    (tenantId: Guid)
    (actor: string)
    (action: string)
    (resourceType: string)
    (resourceId: string)
    (details: Map<string, string>)
    =
    withConnection state (insertAuditRecord tenantId actor action resourceType resourceId details)

let writeUploadAudit (state: AppState) (principal: Principal) (action: string) (uploadId: Guid) (details: Map<string, string>) =
    writeAudit
        state
        principal.TenantId
        principal.Subject
        action
        "upload_session"
        (uploadId.ToString())
        details

let readAuditRecords (tenantId: Guid) (limit: int) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select audit_id, tenant_id, actor_subject, action, resource_type, resource_id, details::text, occurred_at
from audit_log
where tenant_id = @tenant_id
order by occurred_at desc
limit @limit;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("limit", limit) |> ignore

    use reader = cmd.ExecuteReader()
    let entries = ResizeArray<AuditRecord>()

    let rec loop () =
        if reader.Read() then
            let auditId = reader.GetInt64(0)
            let tenantIdValue = reader.GetGuid(1)
            let actor = reader.GetString(2)
            let action = reader.GetString(3)
            let resourceType = reader.GetString(4)
            let resourceId = reader.GetString(5)
            let detailsJson = if reader.IsDBNull(6) then "{}" else reader.GetString(6)
            let occurredAtUtc = reader.GetFieldValue<DateTime>(7) |> toUtcDateTimeOffset

            match tryParseAuditDetails detailsJson with
            | Error err -> Error err
            | Ok details ->
                entries.Add(
                    { AuditId = auditId
                      TenantId = tenantIdValue
                      Actor = actor
                      Action = action
                      ResourceType = resourceType
                      ResourceId = resourceId
                      OccurredAtUtc = occurredAtUtc
                      Details = details }
                )
                loop ()
        else
            Ok(entries |> Seq.toList)

    loop ()

let readScopesFromBindings (tenantId: Guid) (subject: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select r.repo_key, rb.roles
from role_bindings rb
join repos r on r.repo_id = rb.repo_id
where rb.tenant_id = @tenant_id
  and rb.subject = @subject;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("subject", subject) |> ignore

    use reader = cmd.ExecuteReader()
    let scopes = ResizeArray<RepoScope>()

    let rec loop () =
        if reader.Read() then
            let repoKey = reader.GetString(0)
            let roleValues = reader.GetFieldValue<string[]>(1)

            let parseResult =
                roleValues
                |> Array.toList
                |> List.fold
                    (fun acc roleText ->
                        match acc with
                        | Error err -> Error err
                        | Ok () ->
                            match RepoRole.tryParse roleText with
                            | Error err -> Error err
                            | Ok parsedRole ->
                                match RepoScope.tryCreate repoKey parsedRole with
                                | Ok scope ->
                                    scopes.Add(scope)
                                    Ok()
                                | Error err -> Error err)
                    (Ok())

            match parseResult with
            | Error err -> Error $"Invalid persisted role binding value: {err}"
            | Ok () -> loop ()
        else
            Ok(scopes |> Seq.distinctBy RepoScope.value |> Seq.toList)

    loop ()

let upsertRoleBindingForRepo
    (tenantId: Guid)
    (repoKey: string)
    (subject: string)
    (roles: Set<RepoRole>)
    (updatedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
with target_repo as (
  select repo_id
  from repos
  where tenant_id = @tenant_id
    and repo_key = @repo_key
)
insert into role_bindings (tenant_id, repo_id, subject, roles, updated_by_subject, updated_at)
select @tenant_id, target_repo.repo_id, @subject, @roles, @updated_by_subject, now()
from target_repo
on conflict (tenant_id, repo_id, subject)
do update set
  roles = excluded.roles,
  updated_by_subject = excluded.updated_by_subject,
  updated_at = now()
returning updated_at;
""",
            conn
        )

    let roleValues = roles |> Seq.map RepoRole.value |> Seq.toArray
    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("subject", subject) |> ignore
    cmd.Parameters.AddWithValue("updated_by_subject", updatedBySubject) |> ignore

    let rolesParam = cmd.Parameters.Add("roles", NpgsqlDbType.Array ||| NpgsqlDbType.Text)
    rolesParam.Value <- roleValues

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        let updatedAtResult =
            match scalar with
            | :? DateTimeOffset as dto -> Ok dto
            | :? DateTime as dt -> Ok(toUtcDateTimeOffset dt)
            | _ -> Error "Unexpected timestamp type returned for role binding upsert."

        match updatedAtResult with
        | Error err -> Error err
        | Ok updatedAt ->
            Ok(
                Some
                    { RepoKey = repoKey
                      Subject = subject
                      Roles = roles
                      UpdatedAtUtc = updatedAt }
            )

let readRoleBindingsForRepo (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select rb.subject, rb.roles, rb.updated_at
from role_bindings rb
join repos r on r.repo_id = rb.repo_id
where rb.tenant_id = @tenant_id
  and r.repo_key = @repo_key
order by rb.subject;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    use reader = cmd.ExecuteReader()
    let bindings = ResizeArray<RoleBindingRecord>()

    let rec loop () =
        if reader.Read() then
            let subject = reader.GetString(0)
            let roleValues = reader.GetFieldValue<string[]>(1)
            let updatedAt = reader.GetFieldValue<DateTime>(2) |> toUtcDateTimeOffset

            let parseResult =
                roleValues
                |> Array.toList
                |> List.fold
                    (fun acc roleText ->
                        match acc with
                        | Error err -> Error err
                        | Ok roles ->
                            match RepoRole.tryParse roleText with
                            | Error err -> Error err
                            | Ok role -> Ok(Set.add role roles))
                    (Ok Set.empty)

            match parseResult with
            | Error err -> Error $"Invalid persisted role binding value: {err}"
            | Ok roles ->
                bindings.Add(
                    { RepoKey = repoKey
                      Subject = subject
                      Roles = roles
                      UpdatedAtUtc = updatedAt }
                )
                loop ()
        else
            Ok(bindings |> Seq.toList)

    loop ()

let validateRepoRequest (request: CreateRepoRequest) =
    let repoKey = normalizeRepoKey request.RepoKey
    let repoType = normalizeText request.RepoType |> fun value -> value.ToLowerInvariant()
    let upstreamUrl = normalizeText request.UpstreamUrl
    let members = if isNull request.MemberRepos then [||] else request.MemberRepos

    if String.IsNullOrWhiteSpace repoKey then
        Error "repoKey is required."
    else
        match repoType with
        | "local" ->
            Ok
                { RepoKey = repoKey
                  RepoType = repoType
                  UpstreamUrl = None
                  MemberRepos = []
                  CreatedAtUtc = nowUtc ()
                  UpdatedAtUtc = nowUtc () }
        | "remote" ->
            if String.IsNullOrWhiteSpace upstreamUrl then
                Error "Remote repositories require upstreamUrl."
            else
                let isUri, parsedUri = Uri.TryCreate(upstreamUrl, UriKind.Absolute)
                if not isUri then
                    Error "upstreamUrl must be a valid absolute URI."
                else
                    Ok
                        { RepoKey = repoKey
                          RepoType = repoType
                          UpstreamUrl = Some(parsedUri.ToString())
                          MemberRepos = []
                          CreatedAtUtc = nowUtc ()
                          UpdatedAtUtc = nowUtc () }
        | "virtual" ->
            let normalizedMembers =
                members
                |> Array.map normalizeRepoKey
                |> Array.filter (fun memberRepo -> not (String.IsNullOrWhiteSpace memberRepo))
                |> Array.distinct
                |> Array.toList

            if normalizedMembers.IsEmpty then
                Error "Virtual repositories require at least one member repo key."
            else
                Ok
                    { RepoKey = repoKey
                      RepoType = repoType
                      UpstreamUrl = None
                      MemberRepos = normalizedMembers
                      CreatedAtUtc = nowUtc ()
                      UpdatedAtUtc = nowUtc () }
        | _ -> Error "repoType must be one of: local, remote, virtual."

let abortMultipartUploadBestEffort
    (state: AppState)
    (objectStagingKey: string)
    (storageUploadId: string)
    (cancellationToken: Threading.CancellationToken)
    =
    if not (String.IsNullOrWhiteSpace objectStagingKey || String.IsNullOrWhiteSpace storageUploadId) then
        match
            state.ObjectStorageClient.AbortMultipartUpload(objectStagingKey, storageUploadId, cancellationToken).Result
        with
        | _ -> ()

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    let connectionString =
        match builder.Configuration.GetConnectionString("Postgres") with
        | null
        | "" -> "Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress"
        | value -> value

    let state =
        let objectStorageConfig =
            match ObjectStorage.tryReadConfig builder.Configuration with
            | Ok config -> config
            | Error err -> failwith err

        let objectStorageClient = ObjectStorage.createClient objectStorageConfig

        let policyEvaluationTimeoutMs =
            let raw = builder.Configuration.["Policy:EvaluationTimeoutMs"]

            match Int32.TryParse raw with
            | true, value when value > 0 -> value
            | _ -> 250

        { ConnectionString = connectionString
          TenantSlug = "default"
          TenantName = "Default Tenant"
          ObjectStorageClient = objectStorageClient
          PresignPartTtlSeconds = objectStorageConfig.PresignPartTtlSeconds
          PolicyEvaluationTimeoutMs = policyEvaluationTimeoutMs }

    let app = builder.Build()

    app.MapGet(
        "/",
        Func<IResult>(fun () ->
            Results.Ok(
                {| service = "artifortress-api"
                   environment = builder.Environment.EnvironmentName
                   timestampUtc = nowUtc () |}
            ))
    )
    |> ignore

    app.MapGet("/health/live", Func<IResult>(fun () -> Results.Ok({| status = "ok" |}))) |> ignore

    app.MapGet(
        "/health/ready",
        Func<IResult>(fun () ->
            Results.Ok(
                {| status = "ready"
                   dependencies = [| "postgres"; "minio"; "redis" |] |}
            ))
    )
    |> ignore

    app.MapGet(
        "/v1/auth/whoami",
        Func<HttpContext, IResult>(fun ctx ->
            match tryAuthenticate state ctx with
            | Error err -> serviceUnavailable err
            | Ok None -> unauthorized "Missing or invalid bearer token."
            | Ok(Some principal) ->
                let scopeValues = principal.Scopes |> List.map RepoScope.value
                Results.Ok({| subject = principal.Subject; scopes = scopeValues |}))
    )
    |> ignore

    app.MapPost(
        "/v1/auth/pats",
        Func<HttpContext, IResult>(fun ctx ->
            let bootstrapHeader = ctx.Request.Headers.["X-Bootstrap-Token"].ToString()
            let bootstrapSecret = builder.Configuration.["Auth:BootstrapToken"]
            let isBootstrapAuthorized =
                not (String.IsNullOrWhiteSpace bootstrapSecret)
                && secureEquals bootstrapHeader bootstrapSecret

            match tryAuthenticate state ctx with
            | Error err -> serviceUnavailable err
            | Ok caller ->
                let callerIsAdmin =
                    match caller with
                    | Some principal -> Authorization.hasRole principal.Scopes "*" RepoRole.Admin
                    | None -> false

                match withConnection state (fun conn ->
                    ensureTenantId state conn
                    |> Result.bind (fun tenantId ->
                        hasAnyTokensForTenant tenantId conn |> Result.map (fun hasTokens -> tenantId, hasTokens))) with
                | Error err -> serviceUnavailable err
                | Ok(tenantId, hasTokens) ->
                    let allowIssuance = not hasTokens || isBootstrapAuthorized || callerIsAdmin

                    if not allowIssuance then
                        forbidden "PAT issuance requires bootstrap token or admin scope."
                    else
                        match readJsonBody<CreatePatRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            let subject = normalizeSubject request.Subject

                            if String.IsNullOrWhiteSpace subject then
                                badRequest "Subject is required."
                            else
                                let ttlMinutes = request.TtlMinutes

                                if ttlMinutes < 5 || ttlMinutes > 1440 then
                                    badRequest "ttlMinutes must be between 5 and 1440."
                                else
                                    let requestedScopes = if isNull request.Scopes then [||] else request.Scopes
                                    let effectiveScopeInputResult =
                                        if requestedScopes.Length = 0 then
                                            withConnection state (readScopesFromBindings tenantId subject)
                                            |> Result.map (fun derivedScopes -> derivedScopes |> List.map RepoScope.value |> List.toArray)
                                        else
                                            Ok requestedScopes

                                    match effectiveScopeInputResult with
                                    | Error err -> serviceUnavailable err
                                    | Ok effectiveScopeInput ->
                                        if effectiveScopeInput.Length = 0 then
                                            badRequest "No scopes were provided and no role bindings were found for the subject."
                                        else
                                            match parseScopes effectiveScopeInput with
                                            | Error err -> badRequest err
                                            | Ok scopes ->
                                                let rawToken = createPlainToken ()
                                                let tokenHash = toTokenHash rawToken
                                                let tokenId = Guid.NewGuid()
                                                let expiry = (nowUtc ()).AddMinutes(float ttlMinutes)

                                                let actor =
                                                    match caller with
                                                    | Some principal -> principal.Subject
                                                    | None when isBootstrapAuthorized -> "bootstrap"
                                                    | None -> "anonymous"

                                                match withConnection state (insertPat tenantId subject tokenId tokenHash scopes expiry actor) with
                                                | Error err -> serviceUnavailable err
                                                | Ok() ->
                                                    match
                                                        writeAudit
                                                            state
                                                            tenantId
                                                            actor
                                                            "auth.pat.issued"
                                                            "token"
                                                            (tokenId.ToString())
                                                            (Map.ofList [ "subject", subject; "ttlMinutes", ttlMinutes.ToString() ])
                                                    with
                                                    | Error err -> serviceUnavailable err
                                                    | Ok() ->
                                                        Results.Ok(
                                                            {| tokenId = tokenId
                                                               token = rawToken
                                                               subject = subject
                                                               scopes = scopes |> List.map RepoScope.value
                                                               expiresAtUtc = expiry |}
                                                        ))
    )
    |> ignore

    app.MapPost(
        "/v1/auth/pats/revoke",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                match readJsonBody<RevokePatRequest> ctx with
                | Error err -> badRequest err
                | Ok request ->
                    match withConnection state (revokePat principal.TenantId request.TokenId principal.Subject) with
                    | Error err -> serviceUnavailable err
                    | Ok false -> Results.NotFound({| error = "not_found"; message = "Token id was not found." |})
                    | Ok true ->
                        match
                            writeAudit
                                state
                                principal.TenantId
                                principal.Subject
                                "auth.pat.revoked"
                                "token"
                                (request.TokenId.ToString())
                                Map.empty
                        with
                        | Error err -> serviceUnavailable err
                        | Ok() -> Results.Ok({| tokenId = request.TokenId; status = "revoked" |}))
    )
    |> ignore

    app.MapGet(
        "/v1/repos",
        Func<HttpContext, IResult>(fun ctx ->
            match tryAuthenticate state ctx with
            | Error err -> serviceUnavailable err
            | Ok None -> unauthorized "Missing or invalid bearer token."
            | Ok(Some principal) ->
                match withConnection state (readReposForTenant principal.TenantId) with
                | Error err -> serviceUnavailable err
                | Ok repos ->
                    let visibleRepos =
                        repos
                        |> List.filter (fun repo -> Authorization.hasRole principal.Scopes repo.RepoKey RepoRole.Read)
                        |> List.sortBy (fun repo -> repo.RepoKey)
                        |> List.toArray

                    Results.Ok(visibleRepos))
    )
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Read with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (tryReadRepoByKey principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok(Some repo) -> Results.Ok(repo)
                    | Ok None -> Results.NotFound({| error = "not_found"; message = "Repository was not found." |}))
    )
    |> ignore

    app.MapPost(
        "/v1/repos",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                match readJsonBody<CreateRepoRequest> ctx with
                | Error err -> badRequest err
                | Ok request ->
                    match validateRepoRequest request with
                    | Error err -> badRequest err
                    | Ok repo ->
                        match withConnection state (insertRepoForTenant principal.TenantId repo) with
                        | Error err -> serviceUnavailable err
                        | Ok false -> conflict "Repository key already exists."
                        | Ok true ->
                            match
                                writeAudit
                                    state
                                    principal.TenantId
                                    principal.Subject
                                    "repo.created"
                                    "repo"
                                    repo.RepoKey
                                    (Map.ofList [ "repoType", repo.RepoType ])
                            with
                            | Error err -> serviceUnavailable err
                            | Ok() -> Results.Created($"/v1/repos/{repo.RepoKey}", repo))
    )
    |> ignore

    app.MapDelete(
        "/v1/repos/{repoKey}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Admin with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (deleteRepoForTenant principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok true ->
                        match writeAudit state principal.TenantId principal.Subject "repo.deleted" "repo" repoKey Map.empty with
                        | Error err -> serviceUnavailable err
                        | Ok() -> Results.Ok({| repoKey = repoKey; status = "deleted" |})
                    | Ok false ->
                        Results.NotFound({| error = "not_found"; message = "Repository was not found." |}))
    )
    |> ignore

    app.MapPut(
        "/v1/repos/{repoKey}/bindings/{subject}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let subject = normalizeSubject (ctx.Request.RouteValues["subject"].ToString())

            if String.IsNullOrWhiteSpace repoKey || String.IsNullOrWhiteSpace subject then
                badRequest "repoKey and subject route parameters are required."
            else
                match requireRole state ctx repoKey RepoRole.Admin with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok false -> Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                    | Ok true ->
                        match readJsonBody<UpsertRoleBindingRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match parseRoles request.Roles with
                            | Error err -> badRequest err
                            | Ok roles ->
                                match
                                    withConnection state (upsertRoleBindingForRepo principal.TenantId repoKey subject roles principal.Subject)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok None ->
                                    Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                                | Ok(Some binding) ->
                                    let key = $"{repoKey}:{subject}"

                                    match
                                        writeAudit
                                            state
                                            principal.TenantId
                                            principal.Subject
                                            "repo.binding.upserted"
                                            "repo_binding"
                                            key
                                            (Map.ofList [ "roles", roles |> Seq.map RepoRole.value |> String.concat "," ])
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok() ->
                                        Results.Ok(
                                            {| repoKey = binding.RepoKey
                                               subject = binding.Subject
                                               roles = binding.Roles |> Seq.map RepoRole.value |> Seq.toArray
                                               updatedAtUtc = binding.UpdatedAtUtc |}
                                        ))
    )
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/bindings",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Admin with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok false ->
                        Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                    | Ok true ->
                        match withConnection state (readRoleBindingsForRepo principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok bindings ->
                            let response =
                                bindings
                                |> List.sortBy (fun binding -> binding.Subject)
                                |> List.map (fun binding ->
                                    {| repoKey = binding.RepoKey
                                       subject = binding.Subject
                                       roles = binding.Roles |> Seq.map RepoRole.value |> Seq.toArray
                                       updatedAtUtc = binding.UpdatedAtUtc |})
                                |> List.toArray

                            Results.Ok(response))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/packages/versions/drafts",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Write with
                | Error result -> result
                | Ok principal ->
                    match readJsonBody<CreateDraftVersionRequest> ctx with
                    | Error err -> badRequest err
                    | Ok request ->
                        match validateDraftVersionRequest request with
                        | Error err -> badRequest err
                        | Ok(packageType, packageNamespace, packageName, version) ->
                            match
                                withConnection
                                    state
                                    (createOrGetDraftVersionForRepo
                                        principal.TenantId
                                        repoKey
                                        packageType
                                        packageNamespace
                                        packageName
                                        version
                                        principal.Subject)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok RepoMissing ->
                                Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                            | Ok(VersionStateConflict stateValue) ->
                                conflict $"Version already exists in state '{stateValue}' and cannot be reused as a draft."
                            | Ok(DraftCreated draftVersion) ->
                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "package.version.draft.created"
                                        "package_version"
                                        (draftVersion.VersionId.ToString())
                                        (Map.ofList
                                            [ "repoKey", repoKey
                                              "packageType", draftVersion.PackageType
                                              "packageName", draftVersion.PackageName
                                              "version", draftVersion.Version ])
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Created(
                                        $"/v1/repos/{repoKey}/packages/versions/{draftVersion.Version}",
                                        {| versionId = draftVersion.VersionId
                                           repoKey = draftVersion.RepoKey
                                           packageType = draftVersion.PackageType
                                           packageNamespace = draftVersion.PackageNamespace
                                           packageName = draftVersion.PackageName
                                           version = draftVersion.Version
                                           state = draftVersion.State
                                           createdAtUtc = draftVersion.CreatedAtUtc
                                           reusedDraft = false |}
                                    )
                            | Ok(DraftExisting draftVersion) ->
                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "package.version.draft.reused"
                                        "package_version"
                                        (draftVersion.VersionId.ToString())
                                        (Map.ofList
                                            [ "repoKey", repoKey
                                              "packageType", draftVersion.PackageType
                                              "packageName", draftVersion.PackageName
                                              "version", draftVersion.Version ])
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Ok(
                                        {| versionId = draftVersion.VersionId
                                           repoKey = draftVersion.RepoKey
                                           packageType = draftVersion.PackageType
                                           packageNamespace = draftVersion.PackageNamespace
                                           packageName = draftVersion.PackageName
                                           version = draftVersion.Version
                                           state = draftVersion.State
                                           createdAtUtc = draftVersion.CreatedAtUtc
                                           reusedDraft = true |}
                                    )
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/policy/evaluations",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Promote with
                | Error result -> result
                | Ok principal ->
                    match readJsonBody<EvaluatePolicyRequest> ctx with
                    | Error err -> badRequest err
                    | Ok request ->
                        match validateEvaluatePolicyRequest request with
                        | Error err -> badRequest err
                        | Ok(action, versionId, validatedDecision, validatedDecisionSource, reason, policyEngineVersion) ->
                            match
                                tryResolvePolicyDecisionWithTimeout
                                    state.PolicyEvaluationTimeoutMs
                                    validatedDecision
                                    validatedDecisionSource
                                    policyEngineVersion
                            with
                            | Error _ ->
                                let timeoutAuditDetails =
                                    Map.ofList
                                        [ "repoKey", repoKey
                                          "versionId", versionId.ToString()
                                          "action", action
                                          "timeoutMs", state.PolicyEvaluationTimeoutMs.ToString()
                                          "failClosed", "true" ]

                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "policy.timeout"
                                        "policy_evaluation"
                                        (versionId.ToString())
                                        timeoutAuditDetails
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Json(
                                        {| error = "policy_timeout"
                                           message = "Policy evaluation timed out; operation failed closed."
                                           action = action
                                           failClosed = true
                                           timeoutMs = state.PolicyEvaluationTimeoutMs |},
                                        statusCode = StatusCodes.Status503ServiceUnavailable
                                    )
                            | Ok(decision, decisionSource) ->
                                match
                                    withConnection
                                        state
                                        (evaluatePolicyForVersion
                                            principal.TenantId
                                            repoKey
                                            versionId
                                            action
                                            decision
                                            decisionSource
                                            reason
                                            policyEngineVersion
                                            principal.Subject)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok None ->
                                    Results.NotFound(
                                        {| error = "not_found"
                                           message = "Package version was not found in repository." |}
                                    )
                                | Ok(Some evaluation) ->
                                    let auditDetails =
                                        [ "repoKey", repoKey
                                          "versionId", evaluation.VersionId.ToString()
                                          "action", evaluation.Action
                                          "decision", evaluation.Decision
                                          "decisionSource", evaluation.DecisionSource ]
                                        |> (fun values ->
                                            match evaluation.PolicyEngineVersion with
                                            | Some value -> ("policyEngineVersion", value) :: values
                                            | None -> values)
                                        |> (fun values ->
                                            match evaluation.QuarantineId with
                                            | Some value -> ("quarantineId", value.ToString()) :: values
                                            | None -> values)
                                        |> Map.ofList

                                    match
                                        writeAudit
                                            state
                                            principal.TenantId
                                            principal.Subject
                                            "policy.evaluated"
                                            "policy_evaluation"
                                            (evaluation.EvaluationId.ToString())
                                            auditDetails
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok() ->
                                        Results.Created(
                                            $"/v1/repos/{repoKey}/policy/evaluations/{evaluation.EvaluationId}",
                                            {| evaluationId = evaluation.EvaluationId
                                               repoKey = repoKey
                                               versionId = evaluation.VersionId
                                               action = evaluation.Action
                                               decision = evaluation.Decision
                                               decisionSource = evaluation.DecisionSource
                                               reason = evaluation.Reason
                                               policyEngineVersion = evaluation.PolicyEngineVersion
                                               evaluatedAtUtc = evaluation.EvaluatedAtUtc
                                               quarantineId = evaluation.QuarantineId
                                               quarantined = evaluation.QuarantineId.IsSome |}
                                        )
    ))
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/quarantine",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let statusFilterRaw = ctx.Request.Query["status"].ToString()

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match validateQuarantineStatusFilter statusFilterRaw with
                | Error err -> badRequest err
                | Ok statusFilter ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match withConnection state (readQuarantineItemsForRepo principal.TenantId repoKey statusFilter) with
                            | Error err -> serviceUnavailable err
                            | Ok items ->
                                let response =
                                    items
                                    |> List.map (fun item ->
                                        {| quarantineId = item.QuarantineId
                                           repoKey = item.RepoKey
                                           versionId = item.VersionId
                                           status = item.Status
                                           reason = item.Reason
                                           createdAtUtc = item.CreatedAtUtc
                                           resolvedAtUtc = item.ResolvedAtUtc
                                           resolvedBySubject = item.ResolvedBySubject |})
                                    |> List.toArray

                                Results.Ok(response))
    )
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/quarantine/{quarantineId}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let quarantineIdRaw = normalizeText (ctx.Request.RouteValues["quarantineId"].ToString())

            let parsedQuarantineId =
                match Guid.TryParse(quarantineIdRaw) with
                | true, quarantineId -> Ok quarantineId
                | _ -> Error "quarantineId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedQuarantineId with
                | Error err -> badRequest err
                | Ok quarantineId ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match
                                withConnection
                                    state
                                    (tryReadQuarantineItemForRepo principal.TenantId repoKey quarantineId)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok None ->
                                Results.NotFound({| error = "not_found"; message = "Quarantine item was not found." |})
                            | Ok(Some item) ->
                                Results.Ok(
                                    {| quarantineId = item.QuarantineId
                                       repoKey = item.RepoKey
                                       versionId = item.VersionId
                                       status = item.Status
                                       reason = item.Reason
                                       createdAtUtc = item.CreatedAtUtc
                                       resolvedAtUtc = item.ResolvedAtUtc
                                       resolvedBySubject = item.ResolvedBySubject |}
                                ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/quarantine/{quarantineId}/release",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let quarantineIdRaw = normalizeText (ctx.Request.RouteValues["quarantineId"].ToString())

            let parsedQuarantineId =
                match Guid.TryParse(quarantineIdRaw) with
                | true, quarantineId -> Ok quarantineId
                | _ -> Error "quarantineId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedQuarantineId with
                | Error err -> badRequest err
                | Ok quarantineId ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match
                                withConnection
                                    state
                                    (resolveQuarantineItemForRepo
                                        principal.TenantId
                                        repoKey
                                        quarantineId
                                        "released"
                                        principal.Subject)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok QuarantineMissing ->
                                Results.NotFound({| error = "not_found"; message = "Quarantine item was not found." |})
                            | Ok(QuarantineAlreadyResolved statusValue) ->
                                conflict $"Quarantine item is already in status '{statusValue}'."
                            | Ok(QuarantineResolved item) ->
                                let auditDetails =
                                    Map.ofList
                                        [ "repoKey", repoKey
                                          "versionId", item.VersionId.ToString()
                                          "status", item.Status ]

                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "quarantine.released"
                                        "quarantine_item"
                                        (item.QuarantineId.ToString())
                                        auditDetails
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Ok(
                                        {| quarantineId = item.QuarantineId
                                           repoKey = item.RepoKey
                                           versionId = item.VersionId
                                           status = item.Status
                                           reason = item.Reason
                                           createdAtUtc = item.CreatedAtUtc
                                           resolvedAtUtc = item.ResolvedAtUtc
                                           resolvedBySubject = item.ResolvedBySubject |}
                                    ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/quarantine/{quarantineId}/reject",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let quarantineIdRaw = normalizeText (ctx.Request.RouteValues["quarantineId"].ToString())

            let parsedQuarantineId =
                match Guid.TryParse(quarantineIdRaw) with
                | true, quarantineId -> Ok quarantineId
                | _ -> Error "quarantineId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedQuarantineId with
                | Error err -> badRequest err
                | Ok quarantineId ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match
                                withConnection
                                    state
                                    (resolveQuarantineItemForRepo
                                        principal.TenantId
                                        repoKey
                                        quarantineId
                                        "rejected"
                                        principal.Subject)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok QuarantineMissing ->
                                Results.NotFound({| error = "not_found"; message = "Quarantine item was not found." |})
                            | Ok(QuarantineAlreadyResolved statusValue) ->
                                conflict $"Quarantine item is already in status '{statusValue}'."
                            | Ok(QuarantineResolved item) ->
                                let auditDetails =
                                    Map.ofList
                                        [ "repoKey", repoKey
                                          "versionId", item.VersionId.ToString()
                                          "status", item.Status ]

                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "quarantine.rejected"
                                        "quarantine_item"
                                        (item.QuarantineId.ToString())
                                        auditDetails
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Ok(
                                        {| quarantineId = item.QuarantineId
                                           repoKey = item.RepoKey
                                           versionId = item.VersionId
                                           status = item.Status
                                           reason = item.Reason
                                           createdAtUtc = item.CreatedAtUtc
                                           resolvedAtUtc = item.ResolvedAtUtc
                                           resolvedBySubject = item.ResolvedBySubject |}
                                    ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Write with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok false ->
                        Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                    | Ok true ->
                        match readJsonBody<CreateUploadSessionRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match validateUploadSessionRequest request with
                            | Error err -> badRequest err
                            | Ok(digest, expectedLength) ->
                                let expiresAtUtc = (nowUtc ()).AddMinutes(60.0)

                                match withConnection state (tryReadBlobLengthByDigest digest) with
                                | Error err -> serviceUnavailable err
                                | Ok(Some existingLength) when existingLength <> expectedLength ->
                                    conflict "Digest already exists with a different length."
                                | Ok(Some _) ->
                                    match
                                        withConnection
                                            state
                                            (insertUploadSessionForRepo
                                                principal.TenantId
                                                repoKey
                                                digest
                                                expectedLength
                                                "committed"
                                                None
                                                None
                                                (Some digest)
                                                principal.Subject
                                                expiresAtUtc)
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok None ->
                                        Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                                    | Ok(Some session) ->
                                        match
                                            writeUploadAudit
                                                state
                                                principal
                                                "upload.session.created"
                                                session.UploadId
                                                (Map.ofList [ "repoKey", repoKey; "state", session.State; "deduped", "true" ])
                                        with
                                        | Error err -> serviceUnavailable err
                                        | Ok() ->
                                            Results.Ok(
                                                {| uploadId = session.UploadId
                                                   repoKey = repoKey
                                                   expectedDigest = session.ExpectedDigest
                                                   expectedLength = session.ExpectedLength
                                                   state = session.State
                                                   deduped = true
                                                   expiresAtUtc = session.ExpiresAtUtc |}
                                            )
                                | Ok None ->
                                    let uploadId = Guid.NewGuid()
                                    let objectStagingKey = buildStagingObjectKey principal.TenantId repoKey uploadId

                                    match
                                        state.ObjectStorageClient.StartMultipartUpload(objectStagingKey, ctx.RequestAborted).Result
                                    with
                                    | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                    | Ok storageSession ->
                                        match
                                            withConnection
                                                state
                                                (insertUploadSessionForRepo
                                                    principal.TenantId
                                                    repoKey
                                                    digest
                                                    expectedLength
                                                    "initiated"
                                                    (Some objectStagingKey)
                                                    (Some storageSession.UploadId)
                                                    None
                                                    principal.Subject
                                                    expiresAtUtc)
                                        with
                                        | Error err ->
                                            abortMultipartUploadBestEffort
                                                state
                                                objectStagingKey
                                                storageSession.UploadId
                                                ctx.RequestAborted

                                            serviceUnavailable err
                                        | Ok None ->
                                            abortMultipartUploadBestEffort
                                                state
                                                objectStagingKey
                                                storageSession.UploadId
                                                ctx.RequestAborted

                                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                                        | Ok(Some session) ->
                                            match
                                                writeUploadAudit
                                                    state
                                                    principal
                                                    "upload.session.created"
                                                    session.UploadId
                                                    (Map.ofList [ "repoKey", repoKey; "state", session.State; "deduped", "false" ])
                                            with
                                            | Error err -> serviceUnavailable err
                                            | Ok() ->
                                                Results.Created(
                                                    $"/v1/repos/{repoKey}/uploads/{session.UploadId}",
                                                    {| uploadId = session.UploadId
                                                       repoKey = repoKey
                                                       expectedDigest = session.ExpectedDigest
                                                       expectedLength = session.ExpectedLength
                                                       state = session.State
                                                       deduped = false
                                                       expiresAtUtc = session.ExpiresAtUtc
                                                       objectStagingKey = session.ObjectStagingKey
                                                       storageUploadId = session.StorageUploadId |}
                                                ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads/{uploadId}/parts",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let uploadIdRaw = normalizeText (ctx.Request.RouteValues["uploadId"].ToString())

            let parsedUploadId =
                match Guid.TryParse(uploadIdRaw) with
                | true, uploadId -> Ok uploadId
                | _ -> Error "uploadId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedUploadId with
                | Error err -> badRequest err
                | Ok uploadId ->
                    match requireRole state ctx repoKey RepoRole.Write with
                    | Error result -> result
                    | Ok principal ->
                        match readJsonBody<CreateUploadPartRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match validateUploadPartRequest request with
                            | Error err -> badRequest err
                            | Ok partNumber ->
                                match
                                    withConnection state (tryReadUploadSessionForRepo principal.TenantId repoKey uploadId)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok None ->
                                    Results.NotFound({| error = "not_found"; message = "Upload session was not found." |})
                                | Ok(Some session) ->
                                    if session.ExpiresAtUtc <= nowUtc () then
                                        conflict "Upload session has expired."
                                    else
                                        match session.State with
                                        | "committed"
                                        | "aborted" -> conflict "Upload session is terminal and cannot accept more parts."
                                        | "pending_commit" -> conflict "Upload session is pending commit and cannot accept more parts."
                                        | "initiated"
                                        | "parts_uploading" ->
                                            let objectStagingKey = session.ObjectStagingKey |> Option.defaultValue ""
                                            let storageUploadId = session.StorageUploadId |> Option.defaultValue ""

                                            if String.IsNullOrWhiteSpace objectStagingKey || String.IsNullOrWhiteSpace storageUploadId then
                                                conflict "Upload session storage metadata is incomplete."
                                            else
                                                let urlExpiryUtc =
                                                    (nowUtc ()).AddSeconds(float state.PresignPartTtlSeconds)

                                                match
                                                    state.ObjectStorageClient.PresignUploadPart(
                                                        objectStagingKey,
                                                        storageUploadId,
                                                        partNumber,
                                                        urlExpiryUtc
                                                    )
                                                with
                                                | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                                | Ok presignedPart ->
                                                    let updatedStateResult =
                                                        if session.State = "initiated" then
                                                            withConnection
                                                                state
                                                                (updateUploadSessionState
                                                                    principal.TenantId
                                                                    session.UploadId
                                                                    "initiated"
                                                                    "parts_uploading")
                                                        else
                                                            Ok true

                                                    match updatedStateResult with
                                                    | Error err -> serviceUnavailable err
                                                    | Ok false ->
                                                        conflict "Upload session state changed; retry request."
                                                    | Ok true ->
                                                        match
                                                            writeUploadAudit
                                                                state
                                                                principal
                                                                "upload.part.presigned"
                                                                session.UploadId
                                                                (Map.ofList
                                                                    [ "repoKey", repoKey
                                                                      "partNumber", presignedPart.PartNumber.ToString()
                                                                      "state", "parts_uploading" ])
                                                        with
                                                        | Error err -> serviceUnavailable err
                                                        | Ok() ->
                                                            Results.Ok(
                                                                {| uploadId = session.UploadId
                                                                   repoKey = repoKey
                                                                   state = "parts_uploading"
                                                                   partNumber = presignedPart.PartNumber
                                                                   uploadUrl = presignedPart.Url.ToString()
                                                                   expiresAtUtc = presignedPart.ExpiresAtUtc |}
                                                            )
                                        | _ -> conflict "Upload session state is invalid."
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads/{uploadId}/complete",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let uploadIdRaw = normalizeText (ctx.Request.RouteValues["uploadId"].ToString())

            let parsedUploadId =
                match Guid.TryParse(uploadIdRaw) with
                | true, uploadId -> Ok uploadId
                | _ -> Error "uploadId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedUploadId with
                | Error err -> badRequest err
                | Ok uploadId ->
                    match requireRole state ctx repoKey RepoRole.Write with
                    | Error result -> result
                    | Ok principal ->
                        match readJsonBody<CompleteUploadPartsRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match validateCompleteUploadRequest request with
                            | Error err -> badRequest err
                            | Ok completedParts ->
                                match
                                    withConnection state (tryReadUploadSessionForRepo principal.TenantId repoKey uploadId)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok None ->
                                    Results.NotFound({| error = "not_found"; message = "Upload session was not found." |})
                                | Ok(Some session) ->
                                    if session.ExpiresAtUtc <= nowUtc () then
                                        conflict "Upload session has expired."
                                    else
                                        match session.State with
                                        | "initiated" ->
                                            conflict "Upload session has no uploaded parts to complete."
                                        | "pending_commit" ->
                                            Results.Ok(
                                                {| uploadId = session.UploadId
                                                   repoKey = repoKey
                                                   state = session.State
                                                   expectedDigest = session.ExpectedDigest
                                                   expectedLength = session.ExpectedLength |}
                                            )
                                        | "committed"
                                        | "aborted" -> conflict "Upload session is terminal and cannot be completed."
                                        | "parts_uploading" ->
                                            let objectStagingKey = session.ObjectStagingKey |> Option.defaultValue ""
                                            let storageUploadId = session.StorageUploadId |> Option.defaultValue ""

                                            if String.IsNullOrWhiteSpace objectStagingKey || String.IsNullOrWhiteSpace storageUploadId then
                                                conflict "Upload session storage metadata is incomplete."
                                            else
                                                match
                                                    state.ObjectStorageClient.CompleteMultipartUpload(
                                                        objectStagingKey,
                                                        storageUploadId,
                                                        completedParts,
                                                        ctx.RequestAborted
                                                    ).Result
                                                with
                                                | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                                | Ok() ->
                                                    match
                                                        withConnection
                                                            state
                                                            (updateUploadSessionState
                                                                principal.TenantId
                                                                session.UploadId
                                                                "parts_uploading"
                                                                "pending_commit")
                                                    with
                                                    | Error err -> serviceUnavailable err
                                                    | Ok false ->
                                                        conflict "Upload session state changed; retry request."
                                                    | Ok true ->
                                                        match
                                                            writeUploadAudit
                                                                state
                                                                principal
                                                                "upload.completed"
                                                                session.UploadId
                                                                (Map.ofList [ "repoKey", repoKey; "state", "pending_commit" ])
                                                        with
                                                        | Error err -> serviceUnavailable err
                                                        | Ok() ->
                                                            Results.Ok(
                                                                {| uploadId = session.UploadId
                                                                   repoKey = repoKey
                                                                   state = "pending_commit"
                                                                   expectedDigest = session.ExpectedDigest
                                                                   expectedLength = session.ExpectedLength |}
                                                            )
                                        | _ -> conflict "Upload session state is invalid."
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads/{uploadId}/abort",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let uploadIdRaw = normalizeText (ctx.Request.RouteValues["uploadId"].ToString())

            let parsedUploadId =
                match Guid.TryParse(uploadIdRaw) with
                | true, uploadId -> Ok uploadId
                | _ -> Error "uploadId route parameter must be a valid GUID."

            let abortReasonResult =
                if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > 0L then
                    match readJsonBody<AbortUploadRequest> ctx with
                    | Error err -> Error err
                    | Ok request -> Ok(normalizeAbortReason request.Reason)
                else
                    Ok "client_abort"

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedUploadId with
                | Error err -> badRequest err
                | Ok uploadId ->
                    match abortReasonResult with
                    | Error err -> badRequest err
                    | Ok abortReason ->
                        match requireRole state ctx repoKey RepoRole.Write with
                        | Error result -> result
                        | Ok principal ->
                            match
                                withConnection state (tryReadUploadSessionForRepo principal.TenantId repoKey uploadId)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok None ->
                                Results.NotFound({| error = "not_found"; message = "Upload session was not found." |})
                            | Ok(Some session) ->
                                match session.State with
                                | "committed" -> conflict "Upload session is already committed and cannot be aborted."
                                | "aborted" ->
                                    Results.Ok(
                                        {| uploadId = session.UploadId
                                           repoKey = repoKey
                                           state = "aborted"
                                           reason = abortReason |}
                                    )
                                | "initiated"
                                | "parts_uploading"
                                | "pending_commit" ->
                                    let objectStagingKey = session.ObjectStagingKey |> Option.defaultValue ""
                                    let storageUploadId = session.StorageUploadId |> Option.defaultValue ""

                                    let abortStorageResult =
                                        if String.IsNullOrWhiteSpace objectStagingKey || String.IsNullOrWhiteSpace storageUploadId then
                                            Ok()
                                        else
                                            match
                                                state.ObjectStorageClient.AbortMultipartUpload(
                                                    objectStagingKey,
                                                    storageUploadId,
                                                    ctx.RequestAborted
                                                ).Result
                                            with
                                            | Ok() -> Ok()
                                            | Error(NotFound _) -> Ok()
                                            | Error storageErr -> Error storageErr

                                    match abortStorageResult with
                                    | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                    | Ok() ->
                                        match withConnection state (markUploadSessionAborted principal.TenantId session.UploadId abortReason) with
                                        | Error err -> serviceUnavailable err
                                        | Ok false ->
                                            conflict "Upload session state changed; retry request."
                                        | Ok true ->
                                            match
                                                writeUploadAudit
                                                    state
                                                    principal
                                                    "upload.aborted"
                                                    session.UploadId
                                                    (Map.ofList [ "repoKey", repoKey; "reason", abortReason; "state", "aborted" ])
                                            with
                                            | Error err -> serviceUnavailable err
                                            | Ok() ->
                                                Results.Ok(
                                                    {| uploadId = session.UploadId
                                                       repoKey = repoKey
                                                       state = "aborted"
                                                       reason = abortReason |}
                                                )
                                | _ -> conflict "Upload session state is invalid."
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads/{uploadId}/commit",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let uploadIdRaw = normalizeText (ctx.Request.RouteValues["uploadId"].ToString())

            let parsedUploadId =
                match Guid.TryParse(uploadIdRaw) with
                | true, uploadId -> Ok uploadId
                | _ -> Error "uploadId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedUploadId with
                | Error err -> badRequest err
                | Ok uploadId ->
                    match requireRole state ctx repoKey RepoRole.Write with
                    | Error result -> result
                    | Ok principal ->
                        match
                            withConnection state (tryReadUploadSessionForRepo principal.TenantId repoKey uploadId)
                        with
                        | Error err -> serviceUnavailable err
                        | Ok None ->
                            Results.NotFound({| error = "not_found"; message = "Upload session was not found." |})
                        | Ok(Some session) ->
                            if session.ExpiresAtUtc <= nowUtc () then
                                conflict "Upload session has expired."
                            else
                                match session.State with
                                | "initiated"
                                | "parts_uploading" ->
                                    conflict "Upload session must be completed before commit."
                                | "aborted" -> conflict "Upload session is aborted and cannot be committed."
                                | "committed" ->
                                    Results.Ok(
                                        {| uploadId = session.UploadId
                                           repoKey = repoKey
                                           state = session.State
                                           digest = session.CommittedBlobDigest
                                           length = session.ExpectedLength |}
                                    )
                                | "pending_commit" ->
                                    let objectStagingKey = session.ObjectStagingKey |> Option.defaultValue ""

                                    if String.IsNullOrWhiteSpace objectStagingKey then
                                        conflict "Upload session storage metadata is incomplete."
                                    else
                                        match
                                            state.ObjectStorageClient.DownloadObject(
                                                objectStagingKey,
                                                None,
                                                ctx.RequestAborted
                                            ).Result
                                        with
                                        | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                        | Ok downloaded ->
                                            try
                                                let actualDigest, actualLength = computeSha256AndLength downloaded.Stream
                                                downloaded.Dispose()

                                                if actualDigest <> session.ExpectedDigest || actualLength <> session.ExpectedLength then
                                                    let reason =
                                                        if actualDigest <> session.ExpectedDigest then
                                                            "digest_mismatch"
                                                        else
                                                            "length_mismatch"

                                                    match
                                                        withConnection
                                                            state
                                                            (markUploadSessionAborted principal.TenantId session.UploadId reason)
                                                    with
                                                    | Error err -> serviceUnavailable err
                                                    | Ok _ ->
                                                        match
                                                            writeUploadAudit
                                                                state
                                                                principal
                                                                "upload.commit.verification_failed"
                                                                session.UploadId
                                                                (Map.ofList
                                                                    [ "repoKey", repoKey
                                                                      "reason", reason
                                                                      "expectedDigest", session.ExpectedDigest
                                                                      "actualDigest", actualDigest
                                                                      "expectedLength", session.ExpectedLength.ToString()
                                                                      "actualLength", actualLength.ToString() ])
                                                        with
                                                        | Error err -> serviceUnavailable err
                                                        | Ok() ->
                                                            Results.Json(
                                                                {| error = "upload_verification_failed"
                                                                   message = "Uploaded content failed digest/length verification."
                                                                   expectedDigest = session.ExpectedDigest
                                                                   actualDigest = actualDigest
                                                                   expectedLength = session.ExpectedLength
                                                                   actualLength = actualLength |},
                                                                statusCode = StatusCodes.Status409Conflict
                                                            )
                                                else
                                                    match
                                                        withConnection
                                                            state
                                                            (commitUploadSessionWithBlob
                                                                principal.TenantId
                                                                session.UploadId
                                                                session.ExpectedDigest
                                                                session.ExpectedLength
                                                                objectStagingKey
                                                                downloaded.ETag)
                                                    with
                                                    | Error err ->
                                                        if err = "Digest already exists with a different length." then
                                                            conflict err
                                                        else
                                                            serviceUnavailable err
                                                    | Ok false ->
                                                        conflict "Upload session state changed; retry request."
                                                    | Ok true ->
                                                        match
                                                            writeUploadAudit
                                                                state
                                                                principal
                                                                "upload.committed"
                                                                session.UploadId
                                                                (Map.ofList
                                                                    [ "repoKey", repoKey
                                                                      "digest", session.ExpectedDigest
                                                                      "length", session.ExpectedLength.ToString()
                                                                      "state", "committed" ])
                                                        with
                                                        | Error err -> serviceUnavailable err
                                                        | Ok() ->
                                                            Results.Ok(
                                                                {| uploadId = session.UploadId
                                                                   repoKey = repoKey
                                                                   state = "committed"
                                                                   digest = session.ExpectedDigest
                                                                   length = session.ExpectedLength |}
                                                            )
                                            with ex ->
                                                downloaded.Dispose()
                                                serviceUnavailable $"Upload verification failed: {ex.Message}"
                                | _ -> conflict "Upload session state is invalid."
    ))
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/blobs/{digest}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let digest = normalizeDigest (ctx.Request.RouteValues["digest"].ToString())
            let rangeHeader = ctx.Request.Headers.Range.ToString()

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            elif digest.Length <> 64 || not (digest |> Seq.forall isHexChar) then
                badRequest "digest route parameter must be a 64-character lowercase hex SHA-256 digest."
            else
                match parseSingleRangeHeader rangeHeader with
                | Error err -> badRequest err
                | Ok parsedRange ->
                    match requireRole state ctx repoKey RepoRole.Read with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match withConnection state (repoHasCommittedBlobDigest principal.TenantId repoKey digest) with
                            | Error err -> serviceUnavailable err
                            | Ok false ->
                                Results.NotFound({| error = "not_found"; message = "Blob was not found." |})
                            | Ok true ->
                                match withConnection state (repoBlobDigestBlockedByQuarantine principal.TenantId repoKey digest) with
                                | Error err -> serviceUnavailable err
                                | Ok true ->
                                    Results.Json(
                                        {| error = "quarantined_blob"
                                           message = "Blob is unavailable because a linked package version is quarantined." |},
                                        statusCode = StatusCodes.Status423Locked
                                    )
                                | Ok false ->
                                    match withConnection state (tryReadBlobStorageKeyByDigest digest) with
                                    | Error err -> serviceUnavailable err
                                    | Ok None ->
                                        Results.NotFound({| error = "not_found"; message = "Blob was not found." |})
                                    | Ok(Some storageKey) ->
                                        match
                                            state.ObjectStorageClient.DownloadObject(storageKey, parsedRange, ctx.RequestAborted).Result
                                        with
                                        | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                        | Ok downloaded ->
                                            ctx.Response.Headers.["Accept-Ranges"] <- "bytes"

                                            match downloaded.ETag with
                                            | Some etagValue -> ctx.Response.Headers.["ETag"] <- etagValue
                                            | None -> ()

                                            match downloaded.ContentRange with
                                            | Some contentRangeValue -> ctx.Response.Headers.["Content-Range"] <- contentRangeValue
                                            | None -> ()

                                            if downloaded.ContentLength >= 0L then
                                                ctx.Response.ContentLength <- downloaded.ContentLength

                                            ctx.Response.RegisterForDispose(
                                                { new IDisposable with
                                                    member _.Dispose() = downloaded.Dispose() }
                                            )

                                            ctx.Response.StatusCode <- int downloaded.StatusCode

                                            Results.Stream(
                                                downloaded.Stream,
                                                contentType = (downloaded.ContentType |> Option.defaultValue "application/octet-stream")
                                            )
    ))
    |> ignore

    app.MapGet(
        "/v1/audit",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                let rawLimit = ctx.Request.Query["limit"].ToString()

                let limit =
                    match Int32.TryParse rawLimit with
                    | true, parsed when parsed > 0 -> min parsed 500
                    | _ -> 100

                match withConnection state (readAuditRecords principal.TenantId limit) with
                | Error err -> serviceUnavailable err
                | Ok entries -> Results.Ok(entries |> List.toArray))
    )
    |> ignore

    app.Run()
    0
