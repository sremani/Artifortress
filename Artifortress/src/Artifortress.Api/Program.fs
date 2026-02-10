open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Artifortress.Domain
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

let toTokenHash (rawToken: string) =
    use hasher = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(rawToken)
    hasher.ComputeHash(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

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

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    let connectionString =
        match builder.Configuration.GetConnectionString("Postgres") with
        | null
        | "" -> "Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress"
        | value -> value

    let state =
        { ConnectionString = connectionString
          TenantSlug = "default"
          TenantName = "Default Tenant" }

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
                && String.Equals(bootstrapHeader, bootstrapSecret, StringComparison.Ordinal)

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
