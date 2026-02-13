module ApiIntegrationTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Artifortress.Worker
open Npgsql
open NpgsqlTypes
open Xunit
open Xunit.Sdk

[<CLIMutable>]
type PatIssueRequest = {
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
type ArtifactEntryRequest = {
    RelativePath: string
    BlobDigest: string
    ChecksumSha1: string
    ChecksumSha256: string
    SizeBytes: int64
}

[<CLIMutable>]
type UpsertArtifactEntriesRequest = {
    Entries: ArtifactEntryRequest array
}

[<CLIMutable>]
type UpsertManifestRequest = {
    Manifest: JsonElement
    ManifestBlobDigest: string
}

[<CLIMutable>]
type TombstoneVersionRequest = {
    Reason: string
    RetentionDays: int
}

[<CLIMutable>]
type RunGcRequest = {
    DryRun: bool
    RetentionGraceHours: int
    BatchSize: int
}

[<CLIMutable>]
type EvaluatePolicyRequest = {
    Action: string
    VersionId: Guid
    DecisionHint: string
    Reason: string
    PolicyEngineVersion: string
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

let private readResponseBody (response: HttpResponseMessage) =
    response.Content.ReadAsStringAsync().Result

let private ensureStatus (expected: HttpStatusCode) (response: HttpResponseMessage) =
    let body = readResponseBody response

    Assert.True(
        response.StatusCode = expected,
        $"Expected HTTP {(int expected)} but got {(int response.StatusCode)}. Body: {body}"
    )

    body

let private makeRepoKey prefix =
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 10)
    $"{prefix}-{suffix}".ToLowerInvariant()

let private makeSubject prefix =
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
    $"{prefix}-{suffix}".ToLowerInvariant()

let private findRepoRoot () =
    let rec loop (currentPath: string) =
        if File.Exists(Path.Combine(currentPath, "Artifortress.sln")) then
            Some currentPath
        else
            let parent = Directory.GetParent(currentPath)
            if isNull parent then None else loop parent.FullName

    loop AppContext.BaseDirectory

let private chooseFreePort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

let private tokenHashFor (rawToken: string) =
    use hasher = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(rawToken)
    hasher.ComputeHash(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private toBase64Url (bytes: byte array) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private issueOidcToken
    (issuer: string)
    (audience: string)
    (hs256SharedSecret: string)
    (subject: string)
    (scopes: string array)
    (expiresAtUtc: DateTimeOffset)
    =
    let headerJson = """{"alg":"HS256","typ":"JWT"}"""
    let scopeText = scopes |> String.concat " "

    let payloadJson =
        JsonSerializer.Serialize(
            {| iss = issuer
               sub = subject
               aud = audience
               exp = expiresAtUtc.ToUnixTimeSeconds()
               scope = scopeText |}
        )

    let headerSegment = headerJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let payloadSegment = payloadJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let signedPayload = $"{headerSegment}.{payloadSegment}"

    let signatureSegment =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(hs256SharedSecret))
        signedPayload |> Encoding.UTF8.GetBytes |> hmac.ComputeHash |> toBase64Url

    $"{signedPayload}.{signatureSegment}"

let private issueOidcTokenWithGroups
    (issuer: string)
    (audience: string)
    (hs256SharedSecret: string)
    (subject: string)
    (groups: string array)
    (expiresAtUtc: DateTimeOffset)
    =
    let headerJson = """{"alg":"HS256","typ":"JWT"}"""

    let payloadJson =
        JsonSerializer.Serialize(
            {| iss = issuer
               sub = subject
               aud = audience
               exp = expiresAtUtc.ToUnixTimeSeconds()
               groups = groups |}
        )

    let headerSegment = headerJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let payloadSegment = payloadJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let signedPayload = $"{headerSegment}.{payloadSegment}"

    let signatureSegment =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(hs256SharedSecret))
        signedPayload |> Encoding.UTF8.GetBytes |> hmac.ComputeHash |> toBase64Url

    $"{signedPayload}.{signatureSegment}"

let private createIntegrationRs256KeyMaterial () =
    use rsa = RSA.Create(2048)
    rsa.ExportParameters(true), rsa.ExportParameters(false)

let private integrationOidcRs256Kid = "integration-rs256-k1"
let private integrationOidcRs256PrivateParameters, integrationOidcRs256PublicParameters = createIntegrationRs256KeyMaterial ()

let private integrationOidcRs256JwksJson =
    let n = toBase64Url integrationOidcRs256PublicParameters.Modulus
    let e = toBase64Url integrationOidcRs256PublicParameters.Exponent
    $"{{\"keys\":[{{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"{integrationOidcRs256Kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}]}}"

let private issueOidcRs256Token
    (issuer: string)
    (audience: string)
    (subject: string)
    (scopes: string array)
    (expiresAtUtc: DateTimeOffset)
    (kid: string)
    =
    let headerJson = JsonSerializer.Serialize({| alg = "RS256"; typ = "JWT"; kid = kid |})
    let scopeText = scopes |> String.concat " "

    let payloadJson =
        JsonSerializer.Serialize(
            {| iss = issuer
               sub = subject
               aud = audience
               exp = expiresAtUtc.ToUnixTimeSeconds()
               scope = scopeText |}
        )

    let headerSegment = headerJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let payloadSegment = payloadJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let signedPayload = $"{headerSegment}.{payloadSegment}"

    let signatureSegment =
        use rsa = RSA.Create()
        rsa.ImportParameters(integrationOidcRs256PrivateParameters)

        rsa.SignData(
            Encoding.UTF8.GetBytes(signedPayload),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )
        |> toBase64Url

    $"{signedPayload}.{signatureSegment}"

let private buildSamlResponseXml (issuer: string) (audience: string) (subject: string) (groups: string array) =
    let groupValues =
        groups
        |> Array.map (fun group -> $"<saml:AttributeValue>{group}</saml:AttributeValue>")
        |> String.concat ""

    $"""<samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">
<saml:Issuer>{issuer}</saml:Issuer>
<saml:Assertion>
  <saml:Issuer>{issuer}</saml:Issuer>
  <saml:Subject><saml:NameID>{subject}</saml:NameID></saml:Subject>
  <saml:Conditions>
    <saml:AudienceRestriction><saml:Audience>{audience}</saml:Audience></saml:AudienceRestriction>
  </saml:Conditions>
  <saml:AttributeStatement>
    <saml:Attribute Name="groups">{groupValues}</saml:Attribute>
  </saml:AttributeStatement>
</saml:Assertion>
</samlp:Response>"""

let private ensureTenantId (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
insert into tenants (slug, name)
values ('default', 'Default Tenant')
on conflict (slug) do update set name = excluded.name
returning tenant_id;
""",
            conn
        )

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        failwith "Could not resolve tenant id in test fixture."
    else
        scalar :?> Guid

let private ensureMigrationTable (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
create table if not exists schema_migrations (
  version text primary key,
  applied_at timestamptz not null default now()
);
""",
            conn
        )

    cmd.ExecuteNonQuery() |> ignore

let private readAppliedMigrations (conn: NpgsqlConnection) =
    use cmd = new NpgsqlCommand("select version from schema_migrations;", conn)
    use reader = cmd.ExecuteReader()
    let versions = ResizeArray<string>()

    let rec loop () =
        if reader.Read() then
            versions.Add(reader.GetString(0))
            loop ()

    loop ()
    versions |> Seq.toList |> Set.ofList

let private applyMigrations (conn: NpgsqlConnection) (repoRoot: string) =
    ensureMigrationTable conn

    let migrationDir = Path.Combine(repoRoot, "db", "migrations")
    let files = Directory.GetFiles(migrationDir, "*.sql") |> Array.sort
    let applied = readAppliedMigrations conn

    for filePath in files do
        let version = Path.GetFileName(filePath)

        if not (Set.contains version applied) then
            let sql = File.ReadAllText(filePath)
            use tx = conn.BeginTransaction()
            use applyCmd = new NpgsqlCommand(sql, conn, tx)
            applyCmd.ExecuteNonQuery() |> ignore

            use migrationCmd =
                new NpgsqlCommand("insert into schema_migrations (version) values (@version);", conn, tx)

            migrationCmd.Parameters.AddWithValue("version", version) |> ignore
            migrationCmd.ExecuteNonQuery() |> ignore
            tx.Commit()

let private logsToText (logs: ConcurrentQueue<string>) =
    logs |> Seq.toList |> List.truncate 80 |> String.concat Environment.NewLine

type ApiFixture() =
    let connectionString =
        match Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") with
        | null
        | "" -> "Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress"
        | value -> value

    let bootstrapToken = "phase1-bootstrap-token"
    let oidcIssuer = "https://integration-oidc.artifortress.local"
    let oidcAudience = "artifortress-api"
    let oidcSharedSecret = "integration-phase7-oidc-secret"
    let oidcRoleMappings = "groups|af-admins|*|admin"
    let samlIdpMetadataUrl = "https://integration-idp.artifortress.local/metadata"
    let samlExpectedIssuer = "https://integration-idp.artifortress.local/issuer"
    let samlServiceProviderEntityId = "urn:artifortress:integration:sp"
    let samlRoleMappings = "groups|af-admins|*|admin"
    let output = ConcurrentQueue<string>()
    let mutable client: HttpClient option = None
    let mutable apiProcessHandle: Process option = None
    let mutable isAvailable = false
    let mutable unavailableReason = "Fixture not initialized."

    let appendLog prefix (line: string) =
        if not (String.IsNullOrWhiteSpace line) then
            output.Enqueue($"{prefix}{line}")

    let waitForApiReadiness (httpClient: HttpClient) (apiProcess: Process) =
        let deadline = DateTime.UtcNow.AddSeconds(30.0)
        let mutable ready = false

        while (not ready) && DateTime.UtcNow < deadline do
            if apiProcess.HasExited then
                ready <- false
            else
                try
                    use response = httpClient.GetAsync("/health/live").Result
                    ready <- response.StatusCode = HttpStatusCode.OK
                with _ ->
                    ready <- false

            if not ready then
                Thread.Sleep(250)

        if ready then
            Ok()
        elif apiProcess.HasExited then
            Error($"API process exited early with code {apiProcess.ExitCode}. Logs:{Environment.NewLine}{logsToText output}")
        else
            Error($"API readiness timed out. Logs:{Environment.NewLine}{logsToText output}")

    let initialize () =
        match findRepoRoot () with
        | None -> Error "Could not locate repository root from test runtime directory."
        | Some repoRoot ->
            let apiDll = Path.Combine(repoRoot, "src", "Artifortress.Api", "bin", "Debug", "net10.0", "Artifortress.Api.dll")

            if not (File.Exists apiDll) then
                Error($"API binary was not found at {apiDll}. Run `make build` before tests.")
            else
                let migrationResult =
                    try
                        use migrationConn = new NpgsqlConnection(connectionString)
                        migrationConn.Open()
                        applyMigrations migrationConn repoRoot
                        Ok()
                    with ex ->
                        Error $"Database is unavailable or migrations failed: {ex.Message}"

                migrationResult
                |> Result.bind (fun _ ->
                    let port = chooseFreePort ()
                    let baseUrl = $"http://127.0.0.1:{port}"
                    let startInfo = ProcessStartInfo()
                    startInfo.FileName <- "dotnet"
                    startInfo.ArgumentList.Add(apiDll)
                    startInfo.ArgumentList.Add("--urls")
                    startInfo.ArgumentList.Add(baseUrl)
                    startInfo.WorkingDirectory <- repoRoot
                    startInfo.UseShellExecute <- false
                    startInfo.RedirectStandardOutput <- true
                    startInfo.RedirectStandardError <- true
                    startInfo.Environment["ConnectionStrings__Postgres"] <- connectionString
                    startInfo.Environment["Auth__BootstrapToken"] <- bootstrapToken
                    startInfo.Environment["Auth__Oidc__Enabled"] <- "true"
                    startInfo.Environment["Auth__Oidc__Issuer"] <- oidcIssuer
                    startInfo.Environment["Auth__Oidc__Audience"] <- oidcAudience
                    startInfo.Environment["Auth__Oidc__Hs256SharedSecret"] <- oidcSharedSecret
                    startInfo.Environment["Auth__Oidc__JwksJson"] <- integrationOidcRs256JwksJson
                    startInfo.Environment["Auth__Oidc__RoleMappings"] <- oidcRoleMappings
                    startInfo.Environment["Auth__Saml__Enabled"] <- "true"
                    startInfo.Environment["Auth__Saml__IdpMetadataUrl"] <- samlIdpMetadataUrl
                    startInfo.Environment["Auth__Saml__ExpectedIssuer"] <- samlExpectedIssuer
                    startInfo.Environment["Auth__Saml__ServiceProviderEntityId"] <- samlServiceProviderEntityId
                    startInfo.Environment["Auth__Saml__RoleMappings"] <- samlRoleMappings

                    let proc = new Process()
                    proc.StartInfo <- startInfo

                    let started = proc.Start()

                    if not started then
                        Error "Could not start API process for integration tests."
                    else
                        proc.OutputDataReceived.Add(fun args -> appendLog "" args.Data)
                        proc.ErrorDataReceived.Add(fun args -> appendLog "ERR: " args.Data)
                        proc.BeginOutputReadLine()
                        proc.BeginErrorReadLine()

                        let httpClient = new HttpClient()
                        httpClient.BaseAddress <- Uri(baseUrl)

                        match waitForApiReadiness httpClient proc with
                        | Ok() ->
                            apiProcessHandle <- Some proc
                            client <- Some httpClient
                            Ok()
                        | Error err ->
                            httpClient.Dispose()

                            try
                                if not proc.HasExited then
                                    proc.Kill(true)
                            with _ ->
                                ()

                            Error err)

    do
        match initialize () with
        | Ok() ->
            isAvailable <- true
            unavailableReason <- ""
        | Error err ->
            isAvailable <- false
            unavailableReason <- err

    member _.RequireAvailable() =
        if not isAvailable then
            raise (SkipException.ForSkip($"Skipping API integration tests: {unavailableReason}"))

    member _.Client =
        match client with
        | Some value -> value
        | None -> raise (InvalidOperationException("API fixture is unavailable."))

    member _.BootstrapToken = bootstrapToken
    member _.OidcIssuer = oidcIssuer
    member _.OidcAudience = oidcAudience
    member _.OidcSharedSecret = oidcSharedSecret
    member _.SamlIdpMetadataUrl = samlIdpMetadataUrl
    member _.SamlExpectedIssuer = samlExpectedIssuer
    member _.SamlServiceProviderEntityId = samlServiceProviderEntityId

    member this.InsertTokenDirect (subject: string) (scopes: string array) (expiresAtUtc: DateTimeOffset) (revokedAtUtc: DateTimeOffset option) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        let tenantId = ensureTenantId conn
        let tokenId = Guid.NewGuid()
        let rawToken = Guid.NewGuid().ToString("N")
        let tokenHash = tokenHashFor rawToken

        use insertCmd =
            new NpgsqlCommand(
                """
insert into personal_access_tokens
  (token_id, tenant_id, subject, token_hash, scopes, expires_at, created_by_subject, created_at)
values
  (@token_id, @tenant_id, @subject, @token_hash, @scopes, @expires_at, @created_by_subject, @created_at);
""",
                conn
            )

        insertCmd.Parameters.AddWithValue("token_id", tokenId) |> ignore
        insertCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        insertCmd.Parameters.AddWithValue("subject", subject) |> ignore
        insertCmd.Parameters.AddWithValue("token_hash", tokenHash) |> ignore
        insertCmd.Parameters.AddWithValue("expires_at", expiresAtUtc) |> ignore
        insertCmd.Parameters.AddWithValue("created_by_subject", "integration-tests") |> ignore
        insertCmd.Parameters.AddWithValue("created_at", expiresAtUtc.AddMinutes(-60.0)) |> ignore

        let scopesParam = insertCmd.Parameters.Add("scopes", NpgsqlDbType.Array ||| NpgsqlDbType.Text)
        scopesParam.Value <- scopes
        insertCmd.ExecuteNonQuery() |> ignore

        match revokedAtUtc with
        | Some revokedAt ->
            use revokeCmd =
                new NpgsqlCommand(
                    """
update personal_access_tokens
set revoked_at = @revoked_at
where token_id = @token_id;
""",
                    conn
                )

            revokeCmd.Parameters.AddWithValue("revoked_at", revokedAt) |> ignore
            revokeCmd.Parameters.AddWithValue("token_id", tokenId) |> ignore
            revokeCmd.ExecuteNonQuery() |> ignore
        | None -> ()

        rawToken, tokenId

    member this.ExpireUploadSession(uploadId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
update upload_sessions
set expires_at = now() - interval '5 minutes'
where upload_id = @upload_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore

        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not expire upload session {uploadId} in test fixture."

    member this.PublishVersionForTest(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
update package_versions
set state = 'published',
    published_at = now()
where version_id = @version_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore

        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not publish package version {versionId} in test fixture."

    member this.InsertArtifactEntryForVersion(versionId: Guid, relativePath: string, blobDigest: string, sizeBytes: int64) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
insert into artifact_entries
  (version_id, relative_path, blob_digest, checksum_sha256, size_bytes)
values
  (@version_id, @relative_path, @blob_digest, @checksum_sha256, @size_bytes);
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        cmd.Parameters.AddWithValue("relative_path", relativePath) |> ignore
        cmd.Parameters.AddWithValue("blob_digest", blobDigest) |> ignore
        cmd.Parameters.AddWithValue("checksum_sha256", blobDigest) |> ignore
        cmd.Parameters.AddWithValue("size_bytes", sizeBytes) |> ignore

        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not insert artifact entry for version {versionId}."

    member this.SeedCommittedBlobForRepo(repoKey: string, digest: string, lengthBytes: int64) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        let tenantId = ensureTenantId conn

        use repoCmd =
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

        repoCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        repoCmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
        let repoScalar = repoCmd.ExecuteScalar()

        let repoId =
            match repoScalar with
            | :? Guid as value -> value
            | _ -> failwith $"Could not resolve repo id for {repoKey}."

        let storageKey = $"integration/{repoKey}/{digest}"

        use upsertBlobCmd =
            new NpgsqlCommand(
                """
insert into blobs (digest, length_bytes, storage_key)
values (@digest, @length_bytes, @storage_key)
on conflict (digest)
do update set storage_key = excluded.storage_key;
""",
                conn
            )

        upsertBlobCmd.Parameters.AddWithValue("digest", digest) |> ignore
        upsertBlobCmd.Parameters.AddWithValue("length_bytes", lengthBytes) |> ignore
        upsertBlobCmd.Parameters.AddWithValue("storage_key", storageKey) |> ignore
        upsertBlobCmd.ExecuteNonQuery() |> ignore

        use uploadCmd =
            new NpgsqlCommand(
                """
insert into upload_sessions
  (upload_id, tenant_id, repo_id, expected_digest, expected_length, state, committed_blob_digest, created_by_subject, expires_at, committed_at)
values
  (gen_random_uuid(), @tenant_id, @repo_id, @digest, @length_bytes, 'committed', @digest, 'integration-tests', now() + interval '1 hour', now());
""",
                conn
            )

        uploadCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        uploadCmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
        uploadCmd.Parameters.AddWithValue("digest", digest) |> ignore
        uploadCmd.Parameters.AddWithValue("length_bytes", lengthBytes) |> ignore
        uploadCmd.ExecuteNonQuery() |> ignore

    member this.CountArtifactEntriesForVersion(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select count(*)
from artifact_entries
where version_id = @version_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? int64 as count -> count
        | :? int32 as count -> int64 count
        | _ -> failwith $"Unexpected artifact_entries count scalar for version {versionId}."

    member this.HasManifestForVersion(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select exists(
  select 1
  from manifests
  where version_id = @version_id
);
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? bool as existsValue -> existsValue
        | _ -> failwith $"Unexpected manifests existence scalar for version {versionId}."

    member this.TryReadVersionState(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select state
from package_versions
where version_id = @version_id
limit 1;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let scalar = cmd.ExecuteScalar()

        if isNull scalar || scalar = box DBNull.Value then
            None
        else
            match scalar with
            | :? string as value -> Some value
            | _ -> failwith $"Unexpected package_versions.state scalar for version {versionId}."

    member this.CountVersionPublishedEvents(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        let tenantId = ensureTenantId conn

        use cmd =
            new NpgsqlCommand(
                """
select count(*)
from outbox_events
where tenant_id = @tenant_id
  and aggregate_type = 'package_version'
  and aggregate_id = @aggregate_id
  and event_type = 'version.published';
""",
                conn
            )

        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        cmd.Parameters.AddWithValue("aggregate_id", versionId.ToString()) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? int64 as count -> count
        | :? int32 as count -> int64 count
        | _ -> failwith $"Unexpected outbox event count scalar for version {versionId}."

    member this.CountTombstonesForVersion(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select count(*)
from tombstones
where version_id = @version_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? int64 as count -> count
        | :? int32 as count -> int64 count
        | _ -> failwith $"Unexpected tombstone count scalar for version {versionId}."

    member this.ExpireTombstoneRetention(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
update tombstones
set retention_until = now() - interval '1 hour'
where version_id = @version_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not expire tombstone retention for version {versionId}."

    member this.SetTombstoneRetention(versionId: Guid, retentionUntilUtc: DateTimeOffset) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
update tombstones
set retention_until = @retention_until
where version_id = @version_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        cmd.Parameters.AddWithValue("retention_until", retentionUntilUtc) |> ignore
        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not set tombstone retention for version {versionId}."

    member this.SeedOrphanBlob(digest: string, lengthBytes: int64) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        let storageKey = $"integration/orphan/{digest}"

        use cmd =
            new NpgsqlCommand(
                """
insert into blobs (digest, length_bytes, storage_key)
values (@digest, @length_bytes, @storage_key)
on conflict (digest)
do update set storage_key = excluded.storage_key;
""",
                conn
            )

        cmd.Parameters.AddWithValue("digest", digest) |> ignore
        cmd.Parameters.AddWithValue("length_bytes", lengthBytes) |> ignore
        cmd.Parameters.AddWithValue("storage_key", storageKey) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    member this.BlobExists(digest: string) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select exists(
  select 1
  from blobs
  where digest = @digest
);
""",
                conn
            )

        cmd.Parameters.AddWithValue("digest", digest) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? bool as existsValue -> existsValue
        | _ -> failwith $"Unexpected blob exists scalar for digest {digest}."

    member this.SetBlobCreatedAt(digest: string, createdAtUtc: DateTimeOffset) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
update blobs
set created_at = @created_at
where digest = @digest;
""",
                conn
            )

        cmd.Parameters.AddWithValue("digest", digest) |> ignore
        cmd.Parameters.AddWithValue("created_at", createdAtUtc) |> ignore
        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not set blob created_at for digest {digest}."

    member this.TryMutatePublishedVersionMetadata(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        try
            use cmd =
                new NpgsqlCommand(
                    """
update package_versions
set created_by_subject = created_by_subject || '-mutated'
where version_id = @version_id;
""",
                    conn
                )

            cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
            cmd.ExecuteNonQuery() |> ignore
            true
        with :? PostgresException ->
            false

    member this.CountPolicyEvaluations(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select count(*)
from policy_evaluations
where version_id = @version_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? int64 as count -> count
        | :? int32 as count -> int64 count
        | _ -> failwith $"Unexpected count scalar value for policy evaluations of version {versionId}."

    member this.TryReadLatestPolicyDecision(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select decision
from policy_evaluations
where version_id = @version_id
order by evaluation_id desc
limit 1;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let scalar = cmd.ExecuteScalar()

        if isNull scalar || scalar = box DBNull.Value then
            None
        else
            match scalar with
            | :? string as decision -> Some decision
            | _ -> failwith $"Unexpected decision scalar value for policy evaluations of version {versionId}."

    member this.TryReadQuarantineStatus(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select status
from quarantine_items
where version_id = @version_id
limit 1;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let scalar = cmd.ExecuteScalar()

        if isNull scalar || scalar = box DBNull.Value then
            None
        else
            match scalar with
            | :? string as status -> Some status
            | _ -> failwith $"Unexpected quarantine status scalar value for version {versionId}."

    member this.InsertOutboxEvent(eventType: string, aggregateType: string, aggregateId: string, payloadJson: string) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        let tenantId = ensureTenantId conn
        let eventId = Guid.NewGuid()

        use cmd =
            new NpgsqlCommand(
                """
insert into outbox_events
  (event_id, tenant_id, aggregate_type, aggregate_id, event_type, payload, available_at, occurred_at)
values
  (@event_id, @tenant_id, @aggregate_type, @aggregate_id, @event_type, @payload, now(), now());
""",
                conn
            )

        cmd.Parameters.AddWithValue("event_id", eventId) |> ignore
        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        cmd.Parameters.AddWithValue("aggregate_type", aggregateType) |> ignore
        cmd.Parameters.AddWithValue("aggregate_id", aggregateId) |> ignore
        cmd.Parameters.AddWithValue("event_type", eventType) |> ignore

        let payloadParam = cmd.Parameters.Add("payload", NpgsqlDbType.Jsonb)
        payloadParam.Value <- payloadJson

        cmd.ExecuteNonQuery() |> ignore
        eventId

    member this.SetOutboxEventStateForTest
        (
            eventId: Guid,
            availableAtUtc: DateTimeOffset,
            occurredAtUtc: DateTimeOffset,
            deliveredAtUtc: DateTimeOffset option
        ) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
update outbox_events
set available_at = @available_at,
    occurred_at = @occurred_at,
    delivered_at = @delivered_at
where event_id = @event_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("event_id", eventId) |> ignore
        cmd.Parameters.AddWithValue("available_at", availableAtUtc) |> ignore
        cmd.Parameters.AddWithValue("occurred_at", occurredAtUtc) |> ignore

        let deliveredAtParam = cmd.Parameters.Add("delivered_at", NpgsqlDbType.TimestampTz)
        deliveredAtParam.Value <- (match deliveredAtUtc with | Some value -> box value | None -> box DBNull.Value)

        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not update outbox state for event {eventId}."

    member this.UpsertSearchIndexJobForVersionForTest
        (
            versionId: Guid,
            status: string,
            attempts: int,
            availableAtUtc: DateTimeOffset,
            lastError: string option
        ) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use tenantCmd =
            new NpgsqlCommand(
                """
select tenant_id
from package_versions
where version_id = @version_id
limit 1;
""",
                conn
            )

        tenantCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let tenantScalar = tenantCmd.ExecuteScalar()

        let tenantId =
            match tenantScalar with
            | :? Guid as value -> value
            | _ -> failwith $"Could not resolve tenant for version {versionId}."

        use cmd =
            new NpgsqlCommand(
                """
insert into search_index_jobs (tenant_id, version_id, status, available_at, attempts, last_error)
values (@tenant_id, @version_id, @status, @available_at, @attempts, @last_error)
on conflict (tenant_id, version_id)
do update set
  status = excluded.status,
  available_at = excluded.available_at,
  attempts = excluded.attempts,
  last_error = excluded.last_error,
  updated_at = now();
""",
                conn
            )

        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        cmd.Parameters.AddWithValue("status", status) |> ignore
        cmd.Parameters.AddWithValue("available_at", availableAtUtc) |> ignore
        cmd.Parameters.AddWithValue("attempts", attempts) |> ignore

        let lastErrorParam = cmd.Parameters.Add("last_error", NpgsqlDbType.Text)
        lastErrorParam.Value <- (match lastError with | Some value -> box value | None -> box DBNull.Value)

        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not upsert search_index_job for version {versionId}."

    member this.CountSearchIndexJobsForVersion(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select count(*)
from search_index_jobs
where version_id = @version_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? int64 as count -> count
        | :? int32 as count -> int64 count
        | _ -> failwith $"Unexpected search_index_jobs count scalar value for version {versionId}."

    member this.CountSearchIndexJobsForTenant() =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        let tenantId = ensureTenantId conn

        use cmd =
            new NpgsqlCommand(
                """
select count(*)
from search_index_jobs
where tenant_id = @tenant_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? int64 as count -> count
        | :? int32 as count -> int64 count
        | _ -> failwith "Unexpected tenant search_index_jobs count scalar value."

    member this.ResetSearchPipelineStateForTenant() =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        let tenantId = ensureTenantId conn
        use tx = conn.BeginTransaction()

        use deleteJobsCmd =
            new NpgsqlCommand(
                """
delete from search_index_jobs
where tenant_id = @tenant_id;
""",
                conn,
                tx
            )

        deleteJobsCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        deleteJobsCmd.ExecuteNonQuery() |> ignore

        use deleteEventsCmd =
            new NpgsqlCommand(
                """
delete from outbox_events
where tenant_id = @tenant_id
  and event_type = 'version.published';
""",
                conn,
                tx
            )

        deleteEventsCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        deleteEventsCmd.ExecuteNonQuery() |> ignore

        tx.Commit()

    member this.ResetSearchPipelineStateGlobal() =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()
        use tx = conn.BeginTransaction()

        use deleteJobsCmd =
            new NpgsqlCommand(
                """
delete from search_index_jobs;
""",
                conn,
                tx
            )

        deleteJobsCmd.ExecuteNonQuery() |> ignore

        use deleteEventsCmd =
            new NpgsqlCommand(
                """
delete from outbox_events
where event_type = 'version.published';
""",
                conn,
                tx
            )

        deleteEventsCmd.ExecuteNonQuery() |> ignore
        tx.Commit()

    member this.IsOutboxDelivered(eventId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select delivered_at is not null
from outbox_events
where event_id = @event_id
limit 1;
""",
                conn
            )

        cmd.Parameters.AddWithValue("event_id", eventId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? bool as delivered -> delivered
        | _ -> failwith $"Unexpected outbox delivered scalar value for event {eventId}."

    member this.RunSearchIndexOutboxSweep(batchSize: int) =
        this.RequireAvailable()

        match SearchIndexOutboxProducer.runSweep connectionString batchSize with
        | Ok outcome -> outcome
        | Error err -> failwith $"Search outbox sweep failed in test fixture: {err}"

    member this.RunSearchIndexJobSweep(batchSize: int, maxAttempts: int) =
        this.RequireAvailable()

        match SearchIndexJobProcessor.runSweep connectionString batchSize maxAttempts with
        | Ok outcome -> outcome
        | Error err -> failwith $"Search job sweep failed in test fixture: {err}"

    member this.TryReadSearchIndexJobForVersion(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select status, attempts, last_error
from search_index_jobs
where version_id = @version_id
limit 1;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        use reader = cmd.ExecuteReader()

        if reader.Read() then
            let status = reader.GetString(0)
            let attempts = reader.GetInt32(1)
            let lastError = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
            Some(status, attempts, lastError)
        else
            None

    member this.TryReadSearchIndexJobSchedule(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
select status, attempts, available_at, last_error
from search_index_jobs
where version_id = @version_id
limit 1;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        use reader = cmd.ExecuteReader()

        if reader.Read() then
            let status = reader.GetString(0)
            let attempts = reader.GetInt32(1)
            let availableAtUtc = reader.GetFieldValue<DateTimeOffset>(2)
            let lastError = if reader.IsDBNull(3) then None else Some(reader.GetString(3))
            Some(status, attempts, availableAtUtc, lastError)
        else
            None

    member this.MakeSearchIndexJobAvailableNow(versionId: Guid) =
        this.RequireAvailable()

        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        use cmd =
            new NpgsqlCommand(
                """
update search_index_jobs
set available_at = now() - interval '1 second',
    updated_at = now()
where version_id = @version_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        let rows = cmd.ExecuteNonQuery()

        if rows <> 1 then
            failwith $"Could not make search_index_job available for version {versionId}."

    interface IDisposable with
        member _.Dispose() =
            match client with
            | Some value ->
                value.Dispose()
                client <- None
            | None -> ()

            match apiProcessHandle with
            | Some value ->
                try
                    if not value.HasExited then
                        value.Kill(true)
                        value.WaitForExit(5000) |> ignore
                with _ ->
                    ()

                value.Dispose()
                apiProcessHandle <- None
            | None -> ()

[<CollectionDefinition("api-integration")>]
type ApiIntegrationCollection() =
    interface ICollectionFixture<ApiFixture>

[<Collection("api-integration")>]
[<Trait("Category", "Integration")>]
type Phase1ApiTests(fixture: ApiFixture) =
    let issuePat (subject: string) (scopes: string array) (ttlMinutes: int) =
        fixture.RequireAvailable()

        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/pats")
        request.Headers.Add("X-Bootstrap-Token", fixture.BootstrapToken)

        request.Content <-
            JsonContent.Create(
                { Subject = subject
                  Scopes = scopes
                  TtlMinutes = ttlMinutes }
            )

        use response = fixture.Client.Send(request)
        let body = ensureStatus HttpStatusCode.OK response
        use doc = JsonDocument.Parse(body)
        let token = doc.RootElement.GetProperty("token").GetString()
        let tokenId = doc.RootElement.GetProperty("tokenId").GetGuid()

        if String.IsNullOrWhiteSpace token then
            failwith "Token was missing from PAT issue response."

        token, tokenId

    let revokePat (adminToken: string) (tokenId: Guid) =
        fixture.RequireAvailable()

        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/pats/revoke")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", adminToken)
        request.Content <- JsonContent.Create({ TokenId = tokenId })
        use response = fixture.Client.Send(request)
        ensureStatus HttpStatusCode.OK response |> ignore

    let createRepoWithToken (token: string) (repoKey: string) =
        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/repos")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

        request.Content <-
            JsonContent.Create(
                { RepoKey = repoKey
                  RepoType = "local"
                  UpstreamUrl = ""
                  MemberRepos = [||] }
            )

        fixture.Client.Send(request)

    let createRepoAsAdmin (adminToken: string) (repoKey: string) =
        use response = createRepoWithToken adminToken repoKey
        ensureStatus HttpStatusCode.Created response |> ignore

    let putBinding (token: string) (repoKey: string) (subject: string) (roles: string array) =
        use request = new HttpRequestMessage(HttpMethod.Put, $"/v1/repos/{repoKey}/bindings/{subject}")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Content <- JsonContent.Create({ Roles = roles })
        fixture.Client.Send(request)

    let createUploadSessionWithToken (token: string) (repoKey: string) (expectedDigest: string) (expectedLength: int64) =
        use request = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

        request.Content <-
            JsonContent.Create(
                { ExpectedDigest = expectedDigest
                  ExpectedLength = expectedLength }
            )

        fixture.Client.Send(request)

    let createDraftVersionWithToken
        (token: string)
        (repoKey: string)
        (packageType: string)
        (packageNamespace: string)
        (packageName: string)
        (version: string)
        =
        use request =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/drafts")

        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

        request.Content <-
            JsonContent.Create(
                { PackageType = packageType
                  PackageNamespace = packageNamespace
                  PackageName = packageName
                  Version = version }
            )

        fixture.Client.Send(request)

    let upsertArtifactEntriesWithToken
        (token: string)
        (repoKey: string)
        (versionId: Guid)
        (entries: ArtifactEntryRequest array)
        =
        use request =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/{versionId}/entries")

        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Content <- JsonContent.Create({ Entries = entries })
        fixture.Client.Send(request)

    let upsertManifestWithToken
        (token: string)
        (repoKey: string)
        (versionId: Guid)
        (manifest: JsonElement)
        (manifestBlobDigest: string)
        =
        use request =
            new HttpRequestMessage(HttpMethod.Put, $"/v1/repos/{repoKey}/packages/versions/{versionId}/manifest")

        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

        request.Content <-
            JsonContent.Create(
                { Manifest = manifest
                  ManifestBlobDigest = manifestBlobDigest }
            )

        fixture.Client.Send(request)

    let getManifestWithToken (token: string) (repoKey: string) (versionId: Guid) =
        use request =
            new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}/packages/versions/{versionId}/manifest")

        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let publishVersionWithToken (token: string) (repoKey: string) (versionId: Guid) =
        use request =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/{versionId}/publish")

        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let tombstoneVersionWithToken (token: string) (repoKey: string) (versionId: Guid) (reason: string) (retentionDays: int) =
        use request =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/{versionId}/tombstone")

        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Content <- JsonContent.Create({ Reason = reason; RetentionDays = retentionDays })
        fixture.Client.Send(request)

    let runGcWithToken (token: string) (requestBody: RunGcRequest option) =
        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/gc/runs")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

        match requestBody with
        | Some body -> request.Content <- JsonContent.Create(body)
        | None -> ()

        fixture.Client.Send(request)

    let reconcileBlobsWithToken (token: string) (limit: int) =
        use request = new HttpRequestMessage(HttpMethod.Get, $"/v1/admin/reconcile/blobs?limit={limit}")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let opsSummaryWithToken (token: string) =
        use request = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/ops/summary")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let createPublishedVersionWithBlob (repoKey: string) (writeToken: string) (promoteToken: string) (packageName: string) =
        use draftResponse = createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"
        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let digest = tokenHashFor $"p5-digest-{Guid.NewGuid():N}"
        fixture.SeedCommittedBlobForRepo(repoKey, digest, 512L)

        let entries =
            [| { RelativePath = "lib/package.nupkg"
                 BlobDigest = digest
                 ChecksumSha1 = ""
                 ChecksumSha256 = digest
                 SizeBytes = 512L } |]

        use entriesResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId entries
        ensureStatus HttpStatusCode.OK entriesResponse |> ignore

        use manifestDoc = JsonDocument.Parse("""{"id":"phase5.package","version":"1.0.0"}""")
        let manifest = manifestDoc.RootElement.Clone()
        use manifestResponse = upsertManifestWithToken writeToken repoKey versionId manifest ""
        ensureStatus HttpStatusCode.OK manifestResponse |> ignore

        use publishResponse = publishVersionWithToken promoteToken repoKey versionId
        ensureStatus HttpStatusCode.OK publishResponse |> ignore

        versionId, digest

    let evaluatePolicyWithToken
        (token: string)
        (repoKey: string)
        (action: string)
        (versionId: Guid)
        (decisionHint: string)
        (reason: string)
        (policyEngineVersion: string)
        =
        use request = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/policy/evaluations")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

        request.Content <-
            JsonContent.Create(
                { Action = action
                  VersionId = versionId
                  DecisionHint = decisionHint
                  Reason = reason
                  PolicyEngineVersion = policyEngineVersion }
            )

        fixture.Client.Send(request)

    let listQuarantineWithToken (token: string) (repoKey: string) (statusFilter: string option) =
        let path =
            match statusFilter with
            | Some statusValue -> $"/v1/repos/{repoKey}/quarantine?status={Uri.EscapeDataString(statusValue)}"
            | None -> $"/v1/repos/{repoKey}/quarantine"

        use request = new HttpRequestMessage(HttpMethod.Get, path)
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let getQuarantineItemWithToken (token: string) (repoKey: string) (quarantineId: Guid) =
        use request = new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}/quarantine/{quarantineId}")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let releaseQuarantineItemWithToken (token: string) (repoKey: string) (quarantineId: Guid) =
        use request = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/quarantine/{quarantineId}/release")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let rejectQuarantineItemWithToken (token: string) (repoKey: string) (quarantineId: Guid) =
        use request = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/quarantine/{quarantineId}/reject")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let createUploadPartWithToken (token: string) (repoKey: string) (uploadId: Guid) (partNumber: int) =
        use request = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads/{uploadId}/parts")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Content <- JsonContent.Create({ PartNumber = partNumber })
        fixture.Client.Send(request)

    let completeUploadWithToken (token: string) (repoKey: string) (uploadId: Guid) (parts: UploadCompletedPartRequest array) =
        use request = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads/{uploadId}/complete")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Content <- JsonContent.Create({ Parts = parts })
        fixture.Client.Send(request)

    let abortUploadWithToken (token: string) (repoKey: string) (uploadId: Guid) (reason: string) =
        use request = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads/{uploadId}/abort")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Content <- JsonContent.Create({ Reason = reason })
        fixture.Client.Send(request)

    let commitUploadWithToken (token: string) (repoKey: string) (uploadId: Guid) =
        use request = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads/{uploadId}/commit")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let downloadBlobWithToken (token: string) (repoKey: string) (digest: string) (rangeHeader: string option) =
        use request = new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}/blobs/{digest}")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

        match rangeHeader with
        | Some value -> request.Headers.TryAddWithoutValidation("Range", value) |> ignore
        | None -> ()

        fixture.Client.Send(request)

    let getAuditWithToken (token: string) (limit: int) =
        use request = new HttpRequestMessage(HttpMethod.Get, $"/v1/audit?limit={limit}")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        fixture.Client.Send(request)

    let uploadPartFromPresignedUrl (uploadUrl: string) (payload: byte array) =
        use client = new HttpClient()
        use content = new ByteArrayContent(payload)
        use response = client.PutAsync(Uri(uploadUrl), content).Result

        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected successful upload to pre-signed URL but received {(int response.StatusCode)}."
        )

        let etag =
            if not (isNull response.Headers.ETag) then
                response.Headers.ETag.Tag
            else
                match response.Headers.TryGetValues("ETag") with
                | true, values -> values |> Seq.tryHead |> Option.defaultValue ""
                | _ -> ""

        let normalized = etag.Trim().Trim('"')

        if String.IsNullOrWhiteSpace normalized then
            failwith "ETag header was missing from uploaded part response."

        normalized

    [<Fact>]
    member _.``P1-10 unauthorized caller is rejected`` () =
        fixture.RequireAvailable()
        use response = fixture.Client.GetAsync("/v1/auth/whoami").Result
        ensureStatus HttpStatusCode.Unauthorized response |> ignore

    [<Fact>]
    member _.``P7-01 whoami returns pat auth source for PAT caller`` () =
        fixture.RequireAvailable()

        let subject = makeSubject "p701-pat"
        let token, _ = issuePat subject [| "repo:*:read" |] 60

        use request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        use response = fixture.Client.Send(request)
        let body = ensureStatus HttpStatusCode.OK response
        use doc = JsonDocument.Parse(body)

        Assert.Equal(subject, doc.RootElement.GetProperty("subject").GetString())
        Assert.Equal("pat", doc.RootElement.GetProperty("authSource").GetString())

        let scopes =
            doc.RootElement.GetProperty("scopes").EnumerateArray()
            |> Seq.map (fun element -> element.GetString())
            |> Seq.toArray

        Assert.Contains("repo:*:read", scopes)

    [<Fact>]
    member _.``P7-02 oidc bearer flow supports whoami and admin-scoped repo create`` () =
        fixture.RequireAvailable()

        let subject = makeSubject "p702-oidc"

        let oidcToken =
            issueOidcToken
                fixture.OidcIssuer
                fixture.OidcAudience
                fixture.OidcSharedSecret
                subject
                [| "repo:*:admin" |]
                (DateTimeOffset.UtcNow.AddMinutes(10.0))

        use whoamiRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        whoamiRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", oidcToken)
        use whoamiResponse = fixture.Client.Send(whoamiRequest)
        let whoamiBody = ensureStatus HttpStatusCode.OK whoamiResponse
        use whoamiDoc = JsonDocument.Parse(whoamiBody)

        Assert.Equal(subject, whoamiDoc.RootElement.GetProperty("subject").GetString())
        Assert.Equal("oidc", whoamiDoc.RootElement.GetProperty("authSource").GetString())

        let repoKey = makeRepoKey "p702-oidc-repo"
        use createRepoResponse = createRepoWithToken oidcToken repoKey
        ensureStatus HttpStatusCode.Created createRepoResponse |> ignore

    [<Fact>]
    member _.``P7-02 oidc token with mismatched audience is unauthorized`` () =
        fixture.RequireAvailable()

        let oidcToken =
            issueOidcToken
                fixture.OidcIssuer
                "wrong-audience"
                fixture.OidcSharedSecret
                (makeSubject "p702-oidc-bad-aud")
                [| "repo:*:admin" |]
                (DateTimeOffset.UtcNow.AddMinutes(10.0))

        use request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", oidcToken)
        use response = fixture.Client.Send(request)
        ensureStatus HttpStatusCode.Unauthorized response |> ignore

    [<Fact>]
    member _.``P7-03 oidc rs256 bearer flow supports whoami and repo create`` () =
        fixture.RequireAvailable()

        let subject = makeSubject "p703-oidc-rs256"

        let oidcToken =
            issueOidcRs256Token
                fixture.OidcIssuer
                fixture.OidcAudience
                subject
                [| "repo:*:admin" |]
                (DateTimeOffset.UtcNow.AddMinutes(10.0))
                integrationOidcRs256Kid

        use whoamiRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        whoamiRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", oidcToken)
        use whoamiResponse = fixture.Client.Send(whoamiRequest)
        let whoamiBody = ensureStatus HttpStatusCode.OK whoamiResponse
        use whoamiDoc = JsonDocument.Parse(whoamiBody)
        Assert.Equal(subject, whoamiDoc.RootElement.GetProperty("subject").GetString())
        Assert.Equal("oidc", whoamiDoc.RootElement.GetProperty("authSource").GetString())

        let repoKey = makeRepoKey "p703-rs256-repo"
        use createRepoResponse = createRepoWithToken oidcToken repoKey
        ensureStatus HttpStatusCode.Created createRepoResponse |> ignore

    [<Fact>]
    member _.``P7-03 oidc rs256 token with unknown kid is unauthorized`` () =
        fixture.RequireAvailable()

        let oidcToken =
            issueOidcRs256Token
                fixture.OidcIssuer
                fixture.OidcAudience
                (makeSubject "p703-unknown-kid")
                [| "repo:*:admin" |]
                (DateTimeOffset.UtcNow.AddMinutes(10.0))
                "unknown-rs256-kid"

        use request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", oidcToken)
        use response = fixture.Client.Send(request)
        ensureStatus HttpStatusCode.Unauthorized response |> ignore

    [<Fact>]
    member _.``P7-04 oidc claim-role mapping grants wildcard admin from groups claim`` () =
        fixture.RequireAvailable()

        let mappedToken =
            issueOidcTokenWithGroups
                fixture.OidcIssuer
                fixture.OidcAudience
                fixture.OidcSharedSecret
                (makeSubject "p704-groups-admin")
                [| "af-admins" |]
                (DateTimeOffset.UtcNow.AddMinutes(10.0))

        use whoamiRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        whoamiRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", mappedToken)
        use whoamiResponse = fixture.Client.Send(whoamiRequest)
        let whoamiBody = ensureStatus HttpStatusCode.OK whoamiResponse
        use whoamiDoc = JsonDocument.Parse(whoamiBody)

        let scopes =
            whoamiDoc.RootElement.GetProperty("scopes").EnumerateArray()
            |> Seq.map (fun item -> item.GetString())
            |> Seq.toArray

        Assert.Contains("repo:*:admin", scopes)

        let repoKey = makeRepoKey "p704-mapped-repo"
        use createRepoResponse = createRepoWithToken mappedToken repoKey
        ensureStatus HttpStatusCode.Created createRepoResponse |> ignore

    [<Fact>]
    member _.``P7-04 oidc claim-role mapping denies unmatched group claims`` () =
        fixture.RequireAvailable()

        let unmappedToken =
            issueOidcTokenWithGroups
                fixture.OidcIssuer
                fixture.OidcAudience
                fixture.OidcSharedSecret
                (makeSubject "p704-groups-unmapped")
                [| "af-readers" |]
                (DateTimeOffset.UtcNow.AddMinutes(10.0))

        use request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", unmappedToken)
        use response = fixture.Client.Send(request)
        ensureStatus HttpStatusCode.Unauthorized response |> ignore

    [<Fact>]
    member _.``P7-05 saml metadata endpoint returns service-provider metadata contract`` () =
        fixture.RequireAvailable()

        use response = fixture.Client.GetAsync("/v1/auth/saml/metadata").Result
        let body = ensureStatus HttpStatusCode.OK response
        let contentType = response.Content.Headers.ContentType.MediaType

        Assert.Equal("application/samlmetadata+xml", contentType)
        Assert.Contains(fixture.SamlServiceProviderEntityId, body)
        Assert.Contains("/v1/auth/saml/acs", body)
        Assert.Contains(fixture.SamlIdpMetadataUrl, body)

    [<Fact>]
    member _.``P7-06 saml acs exchange validates assertion and returns scoped bearer token`` () =
        fixture.RequireAvailable()

        let samlResponse =
            buildSamlResponseXml
                fixture.SamlExpectedIssuer
                fixture.SamlServiceProviderEntityId
                (makeSubject "p706-saml-user")
                [| "af-admins" |]
            |> Encoding.UTF8.GetBytes
            |> Convert.ToBase64String

        let formValues =
            [ System.Collections.Generic.KeyValuePair("SAMLResponse", samlResponse)
              System.Collections.Generic.KeyValuePair("RelayState", "p706-relay") ]

        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/saml/acs")
        request.Content <- new FormUrlEncodedContent(formValues)
        use response = fixture.Client.Send(request)
        let body = ensureStatus HttpStatusCode.OK response
        use doc = JsonDocument.Parse(body)

        Assert.Equal("saml", doc.RootElement.GetProperty("authSource").GetString())
        Assert.Equal("p706-relay", doc.RootElement.GetProperty("relayState").GetString())

        let issuedToken = doc.RootElement.GetProperty("token").GetString()
        Assert.False(String.IsNullOrWhiteSpace issuedToken)

        use whoamiRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        whoamiRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", issuedToken)
        use whoamiResponse = fixture.Client.Send(whoamiRequest)
        let whoamiBody = ensureStatus HttpStatusCode.OK whoamiResponse
        use whoamiDoc = JsonDocument.Parse(whoamiBody)

        Assert.Equal("pat", whoamiDoc.RootElement.GetProperty("authSource").GetString())

        let scopes =
            whoamiDoc.RootElement.GetProperty("scopes").EnumerateArray()
            |> Seq.map (fun item -> item.GetString())
            |> Seq.toArray

        Assert.Contains("repo:*:admin", scopes)

    [<Fact>]
    member _.``P7-05 saml acs endpoint rejects invalid base64 payload`` () =
        fixture.RequireAvailable()

        let formValues =
            [ System.Collections.Generic.KeyValuePair("SAMLResponse", "!!!bad-base64!!!")
              System.Collections.Generic.KeyValuePair("RelayState", "p705-relay-invalid") ]

        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/saml/acs")
        request.Content <- new FormUrlEncodedContent(formValues)
        use response = fixture.Client.Send(request)
        ensureStatus HttpStatusCode.BadRequest response |> ignore

    [<Fact>]
    member _.``P7-06 saml acs exchange rejects assertion with mismatched issuer`` () =
        fixture.RequireAvailable()

        let samlResponse =
            buildSamlResponseXml
                "https://wrong-idp.artifortress.local/issuer"
                fixture.SamlServiceProviderEntityId
                (makeSubject "p706-saml-bad-issuer")
                [| "af-admins" |]
            |> Encoding.UTF8.GetBytes
            |> Convert.ToBase64String

        let formValues = [ System.Collections.Generic.KeyValuePair("SAMLResponse", samlResponse) ]

        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/saml/acs")
        request.Content <- new FormUrlEncodedContent(formValues)
        use response = fixture.Client.Send(request)
        ensureStatus HttpStatusCode.Unauthorized response |> ignore

    [<Fact>]
    member _.``P1-10 insufficient scope is forbidden`` () =
        fixture.RequireAvailable()
        let repoKey = makeRepoKey "p110-forbidden"
        let token, _ = issuePat (makeSubject "p110-reader") [| "repo:*:read" |] 60
        use response = createRepoWithToken token repoKey
        ensureStatus HttpStatusCode.Forbidden response |> ignore

    [<Fact>]
    member _.``P1-10 expired token is denied`` () =
        fixture.RequireAvailable()

        let expiredToken, _ =
            fixture.InsertTokenDirect
                (makeSubject "p110-expired")
                [| "repo:*:admin" |]
                (DateTimeOffset.UtcNow.AddMinutes(-10.0))
                None

        use request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", expiredToken)
        use response = fixture.Client.Send(request)
        ensureStatus HttpStatusCode.Unauthorized response |> ignore

    [<Fact>]
    member _.``P1-10 revoked token is denied`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p110-admin") [| "repo:*:admin" |] 60
        let userToken, tokenId = issuePat (makeSubject "p110-user") [| "repo:*:read" |] 60

        revokePat adminToken tokenId

        use request = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/whoami")
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", userToken)
        use response = fixture.Client.Send(request)
        ensureStatus HttpStatusCode.Unauthorized response |> ignore

    [<Fact>]
    member _.``P1-11 repo CRUD covers authorized and denied paths`` () =
        fixture.RequireAvailable()
        let adminToken, _ = issuePat (makeSubject "p111-admin") [| "repo:*:admin" |] 60
        let readToken, _ = issuePat (makeSubject "p111-reader") [| "repo:*:read" |] 60
        let repoKey = makeRepoKey "p111-crud"

        use unauthorizedCreateRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/repos")

        unauthorizedCreateRequest.Content <-
            JsonContent.Create(
                { RepoKey = repoKey
                  RepoType = "local"
                  UpstreamUrl = ""
                  MemberRepos = [||] }
            )

        use unauthorizedCreateResponse = fixture.Client.Send(unauthorizedCreateRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedCreateResponse |> ignore

        use forbiddenCreateResponse = createRepoWithToken readToken repoKey
        ensureStatus HttpStatusCode.Forbidden forbiddenCreateResponse |> ignore

        createRepoAsAdmin adminToken repoKey

        use getRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}")
        getRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", adminToken)
        use getResponse = fixture.Client.Send(getRequest)
        ensureStatus HttpStatusCode.OK getResponse |> ignore

        use listRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/repos")
        listRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", adminToken)
        use listResponse = fixture.Client.Send(listRequest)
        let listBody = ensureStatus HttpStatusCode.OK listResponse
        use listDoc = JsonDocument.Parse(listBody)

        let foundRepo =
            listDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun element -> element.GetProperty("repoKey").GetString() = repoKey)

        Assert.True(foundRepo, $"Expected repo {repoKey} in /v1/repos list.")

        use forbiddenDeleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/v1/repos/{repoKey}")
        forbiddenDeleteRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", readToken)
        use forbiddenDeleteResponse = fixture.Client.Send(forbiddenDeleteRequest)
        ensureStatus HttpStatusCode.Forbidden forbiddenDeleteResponse |> ignore

        use deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/v1/repos/{repoKey}")
        deleteRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", adminToken)
        use deleteResponse = fixture.Client.Send(deleteRequest)
        ensureStatus HttpStatusCode.OK deleteResponse |> ignore

    [<Fact>]
    member _.``P1-11 role binding APIs cover unauthorized, forbidden, and authorized paths`` () =
        fixture.RequireAvailable()
        let adminToken, _ = issuePat (makeSubject "p111-bind-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p111-bindings"
        let bindingSubject = makeSubject "p111-subject"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p111-writer") [| $"repo:{repoKey}:write" |] 60

        use unauthorizedPutRequest = new HttpRequestMessage(HttpMethod.Put, $"/v1/repos/{repoKey}/bindings/{bindingSubject}")
        unauthorizedPutRequest.Content <- JsonContent.Create({ Roles = [| "read"; "write" |] })
        use unauthorizedPutResponse = fixture.Client.Send(unauthorizedPutRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedPutResponse |> ignore

        use forbiddenPutResponse = putBinding writeToken repoKey bindingSubject [| "read"; "write" |]
        ensureStatus HttpStatusCode.Forbidden forbiddenPutResponse |> ignore

        use adminPutResponse = putBinding adminToken repoKey bindingSubject [| "read"; "write" |]
        ensureStatus HttpStatusCode.OK adminPutResponse |> ignore

        use unauthorizedGetBindings = fixture.Client.GetAsync($"/v1/repos/{repoKey}/bindings").Result
        ensureStatus HttpStatusCode.Unauthorized unauthorizedGetBindings |> ignore

        use forbiddenGetBindingsRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}/bindings")
        forbiddenGetBindingsRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", writeToken)
        use forbiddenGetBindingsResponse = fixture.Client.Send(forbiddenGetBindingsRequest)
        ensureStatus HttpStatusCode.Forbidden forbiddenGetBindingsResponse |> ignore

        use adminGetBindingsRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}/bindings")
        adminGetBindingsRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", adminToken)
        use adminGetBindingsResponse = fixture.Client.Send(adminGetBindingsRequest)
        let bindingsBody = ensureStatus HttpStatusCode.OK adminGetBindingsResponse
        use bindingsDoc = JsonDocument.Parse(bindingsBody)

        let foundBinding =
            bindingsDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun element -> element.GetProperty("subject").GetString() = bindingSubject)

        Assert.True(foundBinding, $"Expected binding subject {bindingSubject} in role binding list.")

    [<Fact>]
    member _.``P1-11 create repo rejects repo keys containing colon`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p111-colon-admin") [| "repo:*:admin" |] 60

        use response =
            createRepoWithToken adminToken (makeRepoKey "p111:colon")

        let body = ensureStatus HttpStatusCode.BadRequest response
        use doc = JsonDocument.Parse(body)
        Assert.Equal("bad_request", doc.RootElement.GetProperty("error").GetString())
        Assert.Equal("repoKey cannot contain ':'.", doc.RootElement.GetProperty("message").GetString())

    [<Fact>]
    member _.``P3-02 draft version create API enforces authz and reuses existing draft`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p302-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p302-draft"
        createRepoAsAdmin adminToken repoKey

        let readToken, _ = issuePat (makeSubject "p302-reader") [| $"repo:{repoKey}:read" |] 60
        let writeToken, _ = issuePat (makeSubject "p302-writer") [| $"repo:{repoKey}:write" |] 60
        let packageType = "nuget"
        let packageNamespace = ""
        let packageName = $"widget-{Guid.NewGuid():N}"
        let version = "1.0.0"

        use unauthorizedRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/drafts")

        unauthorizedRequest.Content <-
            JsonContent.Create(
                { PackageType = packageType
                  PackageNamespace = packageNamespace
                  PackageName = packageName
                  Version = version }
            )

        use unauthorizedResponse = fixture.Client.Send(unauthorizedRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedResponse |> ignore

        use forbiddenResponse =
            createDraftVersionWithToken readToken repoKey packageType packageNamespace packageName version

        ensureStatus HttpStatusCode.Forbidden forbiddenResponse |> ignore

        use createResponse =
            createDraftVersionWithToken writeToken repoKey packageType packageNamespace packageName version

        let createBody = ensureStatus HttpStatusCode.Created createResponse
        use createDoc = JsonDocument.Parse(createBody)
        let createdVersionId = createDoc.RootElement.GetProperty("versionId").GetGuid()
        let createdState = createDoc.RootElement.GetProperty("state").GetString()
        let createdReusedFlag = createDoc.RootElement.GetProperty("reusedDraft").GetBoolean()

        Assert.Equal("draft", createdState)
        Assert.False(createdReusedFlag)

        use secondCreateResponse =
            createDraftVersionWithToken writeToken repoKey packageType packageNamespace packageName version

        let secondCreateBody = ensureStatus HttpStatusCode.OK secondCreateResponse
        use secondDoc = JsonDocument.Parse(secondCreateBody)
        let secondVersionId = secondDoc.RootElement.GetProperty("versionId").GetGuid()
        let secondState = secondDoc.RootElement.GetProperty("state").GetString()
        let secondReusedFlag = secondDoc.RootElement.GetProperty("reusedDraft").GetBoolean()

        Assert.Equal(createdVersionId, secondVersionId)
        Assert.Equal("draft", secondState)
        Assert.True(secondReusedFlag)

    [<Fact>]
    member _.``P3-01 published-version immutability trigger rejects metadata mutation`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p301-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p301-immutability"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p301-writer") [| $"repo:{repoKey}:write" |] 60
        let packageType = "nuget"
        let packageNamespace = ""
        let packageName = $"immutability-{Guid.NewGuid():N}"
        let version = "1.0.0"

        use createResponse =
            createDraftVersionWithToken writeToken repoKey packageType packageNamespace packageName version

        let createBody = ensureStatus HttpStatusCode.Created createResponse
        use createDoc = JsonDocument.Parse(createBody)
        let versionId = createDoc.RootElement.GetProperty("versionId").GetGuid()

        fixture.PublishVersionForTest(versionId)

        let mutated = fixture.TryMutatePublishedVersionMetadata(versionId)
        Assert.False(mutated, "Expected metadata mutation on published version to be rejected by DB trigger.")

    [<Fact>]
    member _.``P3-02 draft create returns conflict when version is already published`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p302-published-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p302-published"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p302-published-writer") [| $"repo:{repoKey}:write" |] 60
        let packageType = "nuget"
        let packageNamespace = ""
        let packageName = $"published-{Guid.NewGuid():N}"
        let version = "1.0.0"

        use createResponse =
            createDraftVersionWithToken writeToken repoKey packageType packageNamespace packageName version

        let createBody = ensureStatus HttpStatusCode.Created createResponse
        use createDoc = JsonDocument.Parse(createBody)
        let versionId = createDoc.RootElement.GetProperty("versionId").GetGuid()

        fixture.PublishVersionForTest(versionId)

        use conflictResponse =
            createDraftVersionWithToken writeToken repoKey packageType packageNamespace packageName version

        let conflictBody = ensureStatus HttpStatusCode.Conflict conflictResponse
        Assert.Contains("cannot be reused as a draft", conflictBody)

    [<Fact>]
    member _.``P3-03 artifact entry API validates authz and blob existence`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p303-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p303-entries"
        createRepoAsAdmin adminToken repoKey

        let readToken, _ = issuePat (makeSubject "p303-reader") [| $"repo:{repoKey}:read" |] 60
        let writeToken, _ = issuePat (makeSubject "p303-writer") [| $"repo:{repoKey}:write" |] 60
        let packageName = $"entries-{Guid.NewGuid():N}"

        use draftResponse = createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"
        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let missingDigest = tokenHashFor $"missing-{Guid.NewGuid():N}"
        let entryMissing =
            [| { RelativePath = "lib/main.nupkg"
                 BlobDigest = missingDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = missingDigest
                 SizeBytes = 128L } |]

        use unauthorizedRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/{versionId}/entries")

        unauthorizedRequest.Content <- JsonContent.Create({ Entries = entryMissing })
        use unauthorizedResponse = fixture.Client.Send(unauthorizedRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedResponse |> ignore

        use forbiddenResponse = upsertArtifactEntriesWithToken readToken repoKey versionId entryMissing
        ensureStatus HttpStatusCode.Forbidden forbiddenResponse |> ignore

        use missingBlobResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId entryMissing
        let missingBody = ensureStatus HttpStatusCode.Conflict missingBlobResponse
        Assert.Contains("does not exist", missingBody)

        let committedDigest = tokenHashFor $"committed-{Guid.NewGuid():N}"
        fixture.SeedCommittedBlobForRepo(repoKey, committedDigest, 256L)

        let validEntries =
            [| { RelativePath = "lib/main.nupkg"
                 BlobDigest = committedDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = committedDigest
                 SizeBytes = 256L } |]

        use successResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId validEntries
        ensureStatus HttpStatusCode.OK successResponse |> ignore

        let persistedCount = fixture.CountArtifactEntriesForVersion(versionId)
        Assert.Equal(1L, persistedCount)

    [<Fact>]
    member _.``P3-03 duplicate relative paths are rejected deterministically`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p303-dup-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p303-dup"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p303-dup-writer") [| $"repo:{repoKey}:write" |] 60
        let packageName = $"entries-dup-{Guid.NewGuid():N}"

        use draftResponse = createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"
        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let committedDigest = tokenHashFor $"committed-dup-{Guid.NewGuid():N}"
        fixture.SeedCommittedBlobForRepo(repoKey, committedDigest, 256L)

        let duplicateEntries =
            [| { RelativePath = "lib/main.nupkg"
                 BlobDigest = committedDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = committedDigest
                 SizeBytes = 256L }
               { RelativePath = "lib/main.nupkg"
                 BlobDigest = committedDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = committedDigest
                 SizeBytes = 256L } |]

        use duplicateResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId duplicateEntries
        let duplicateBody = ensureStatus HttpStatusCode.BadRequest duplicateResponse
        Assert.Contains("Duplicate relativePath", duplicateBody)

    [<Fact>]
    member _.``P3-04 manifest API validates per package type and is queryable`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p304-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p304-manifest"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p304-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p304-reader") [| $"repo:{repoKey}:read" |] 60
        let packageName = $"manifest-{Guid.NewGuid():N}"

        use draftResponse = createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"
        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use invalidManifestDoc = JsonDocument.Parse("""{"version":"1.0.0"}""")
        let invalidManifest = invalidManifestDoc.RootElement.Clone()

        use invalidManifestResponse = upsertManifestWithToken writeToken repoKey versionId invalidManifest ""
        let invalidBody = ensureStatus HttpStatusCode.BadRequest invalidManifestResponse
        Assert.Contains("manifest.id is required", invalidBody)

        use validManifestDoc = JsonDocument.Parse("""{"id":"demo.package","version":"1.0.0","authors":["integration"]}""")
        let validManifest = validManifestDoc.RootElement.Clone()

        use putManifestResponse = upsertManifestWithToken writeToken repoKey versionId validManifest ""
        ensureStatus HttpStatusCode.OK putManifestResponse |> ignore

        use getManifestResponse = getManifestWithToken readToken repoKey versionId
        let getBody = ensureStatus HttpStatusCode.OK getManifestResponse
        use getDoc = JsonDocument.Parse(getBody)

        Assert.Equal("nuget", getDoc.RootElement.GetProperty("packageType").GetString())
        Assert.Equal("draft", getDoc.RootElement.GetProperty("state").GetString())
        Assert.Equal("demo.package", getDoc.RootElement.GetProperty("manifest").GetProperty("id").GetString())

        let hasManifest = fixture.HasManifestForVersion(versionId)
        Assert.True(hasManifest, "Expected manifest to be persisted for draft version.")

    [<Fact>]
    member _.``P3-05 P3-06 publish endpoint is atomic, emits outbox event once, and is idempotent`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p305-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p305-publish"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p305-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p305-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"publish-{Guid.NewGuid():N}"

        use draftResponse = createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"
        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let committedDigest = tokenHashFor $"publish-digest-{Guid.NewGuid():N}"
        fixture.SeedCommittedBlobForRepo(repoKey, committedDigest, 512L)

        let entries =
            [| { RelativePath = "lib/package.nupkg"
                 BlobDigest = committedDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = committedDigest
                 SizeBytes = 512L } |]

        use entriesResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId entries
        ensureStatus HttpStatusCode.OK entriesResponse |> ignore

        use manifestDoc = JsonDocument.Parse("""{"id":"demo.publish","version":"1.0.0"}""")
        let manifest = manifestDoc.RootElement.Clone()
        use manifestResponse = upsertManifestWithToken writeToken repoKey versionId manifest ""
        ensureStatus HttpStatusCode.OK manifestResponse |> ignore

        use publishResponse = publishVersionWithToken promoteToken repoKey versionId
        let publishBody = ensureStatus HttpStatusCode.OK publishResponse
        use publishDoc = JsonDocument.Parse(publishBody)
        Assert.Equal("published", publishDoc.RootElement.GetProperty("state").GetString())
        Assert.True(publishDoc.RootElement.GetProperty("eventEmitted").GetBoolean())
        Assert.False(publishDoc.RootElement.GetProperty("idempotent").GetBoolean())

        let publishedState = fixture.TryReadVersionState(versionId)
        let outboxCountAfterFirst = fixture.CountVersionPublishedEvents(versionId)
        Assert.Equal(Some "published", publishedState)
        Assert.Equal(1L, outboxCountAfterFirst)

        use republishResponse = publishVersionWithToken promoteToken repoKey versionId
        let republishBody = ensureStatus HttpStatusCode.OK republishResponse
        use republishDoc = JsonDocument.Parse(republishBody)
        Assert.True(republishDoc.RootElement.GetProperty("idempotent").GetBoolean())
        Assert.False(republishDoc.RootElement.GetProperty("eventEmitted").GetBoolean())

        let outboxCountAfterSecond = fixture.CountVersionPublishedEvents(versionId)
        Assert.Equal(1L, outboxCountAfterSecond)

    [<Fact>]
    member _.``P3-07 publish workflow endpoints enforce authz and emit audit actions`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p307-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p307-authz"
        createRepoAsAdmin adminToken repoKey

        let readToken, _ = issuePat (makeSubject "p307-reader") [| $"repo:{repoKey}:read" |] 60
        let writeToken, _ = issuePat (makeSubject "p307-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p307-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"authz-{Guid.NewGuid():N}"

        use draftResponse = createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"
        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let committedDigest = tokenHashFor $"authz-digest-{Guid.NewGuid():N}"
        fixture.SeedCommittedBlobForRepo(repoKey, committedDigest, 256L)

        let entries =
            [| { RelativePath = "lib/authz.nupkg"
                 BlobDigest = committedDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = committedDigest
                 SizeBytes = 256L } |]

        use unauthorizedEntriesRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/{versionId}/entries")

        unauthorizedEntriesRequest.Content <- JsonContent.Create({ Entries = entries })
        use unauthorizedEntriesResponse = fixture.Client.Send(unauthorizedEntriesRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedEntriesResponse |> ignore

        use forbiddenEntriesResponse = upsertArtifactEntriesWithToken readToken repoKey versionId entries
        ensureStatus HttpStatusCode.Forbidden forbiddenEntriesResponse |> ignore

        use okEntriesResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId entries
        ensureStatus HttpStatusCode.OK okEntriesResponse |> ignore

        use manifestDoc = JsonDocument.Parse("""{"id":"demo.authz","version":"1.0.0"}""")
        let manifest = manifestDoc.RootElement.Clone()

        use unauthorizedManifestRequest =
            new HttpRequestMessage(HttpMethod.Put, $"/v1/repos/{repoKey}/packages/versions/{versionId}/manifest")

        unauthorizedManifestRequest.Content <- JsonContent.Create({ Manifest = manifest; ManifestBlobDigest = "" })
        use unauthorizedManifestResponse = fixture.Client.Send(unauthorizedManifestRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedManifestResponse |> ignore

        use forbiddenManifestResponse = upsertManifestWithToken readToken repoKey versionId manifest ""
        ensureStatus HttpStatusCode.Forbidden forbiddenManifestResponse |> ignore

        use okManifestResponse = upsertManifestWithToken writeToken repoKey versionId manifest ""
        ensureStatus HttpStatusCode.OK okManifestResponse |> ignore

        use unauthorizedPublishRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/{versionId}/publish")

        use unauthorizedPublishResponse = fixture.Client.Send(unauthorizedPublishRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedPublishResponse |> ignore

        use forbiddenPublishResponse = publishVersionWithToken writeToken repoKey versionId
        ensureStatus HttpStatusCode.Forbidden forbiddenPublishResponse |> ignore

        use okPublishResponse = publishVersionWithToken promoteToken repoKey versionId
        ensureStatus HttpStatusCode.OK okPublishResponse |> ignore

        use auditResponse = getAuditWithToken adminToken 200
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let actions =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.map (fun entry -> entry.GetProperty("action").GetString())
            |> Seq.toList

        Assert.Contains("package.version.entries.upserted", actions)
        Assert.Contains("package.version.manifest.upserted", actions)
        Assert.Contains("package.version.published", actions)

    [<Fact>]
    member _.``P3-08 published versions reject deterministic entry and manifest mutation attempts`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p308-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p308-immut"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p308-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p308-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"immut-{Guid.NewGuid():N}"

        use draftResponse = createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"
        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let committedDigest = tokenHashFor $"immut-digest-{Guid.NewGuid():N}"
        fixture.SeedCommittedBlobForRepo(repoKey, committedDigest, 300L)

        let entries =
            [| { RelativePath = "lib/immut.nupkg"
                 BlobDigest = committedDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = committedDigest
                 SizeBytes = 300L } |]

        use entriesResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId entries
        ensureStatus HttpStatusCode.OK entriesResponse |> ignore

        use manifestDoc = JsonDocument.Parse("""{"id":"demo.immut","version":"1.0.0"}""")
        let manifest = manifestDoc.RootElement.Clone()
        use manifestResponse = upsertManifestWithToken writeToken repoKey versionId manifest ""
        ensureStatus HttpStatusCode.OK manifestResponse |> ignore

        use publishResponse = publishVersionWithToken promoteToken repoKey versionId
        ensureStatus HttpStatusCode.OK publishResponse |> ignore

        let mutationEntry =
            [| { RelativePath = "lib/immut.nupkg"
                 BlobDigest = committedDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = committedDigest
                 SizeBytes = 301L } |]

        use entryMutationResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId mutationEntry
        let entryMutationBody = ensureStatus HttpStatusCode.Conflict entryMutationResponse
        Assert.Contains("cannot be modified", entryMutationBody)

        use manifestMutationDoc = JsonDocument.Parse("""{"id":"demo.immut","version":"1.0.1"}""")
        let manifestMutation = manifestMutationDoc.RootElement.Clone()
        use manifestMutationResponse = upsertManifestWithToken writeToken repoKey versionId manifestMutation ""
        let manifestMutationBody = ensureStatus HttpStatusCode.Conflict manifestMutationResponse
        Assert.Contains("cannot be modified", manifestMutationBody)

    [<Fact>]
    member _.``P3-09 publish failure path leaves no partial published state or outbox event`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p309-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p309-atomic"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p309-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p309-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"atomic-{Guid.NewGuid():N}"

        use draftResponse = createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"
        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let committedDigest = tokenHashFor $"atomic-digest-{Guid.NewGuid():N}"
        fixture.SeedCommittedBlobForRepo(repoKey, committedDigest, 412L)

        let entries =
            [| { RelativePath = "lib/atomic.nupkg"
                 BlobDigest = committedDigest
                 ChecksumSha1 = ""
                 ChecksumSha256 = committedDigest
                 SizeBytes = 412L } |]

        use entriesResponse = upsertArtifactEntriesWithToken writeToken repoKey versionId entries
        ensureStatus HttpStatusCode.OK entriesResponse |> ignore

        use firstPublishResponse = publishVersionWithToken promoteToken repoKey versionId
        let firstPublishBody = ensureStatus HttpStatusCode.Conflict firstPublishResponse
        Assert.Contains("without a manifest", firstPublishBody)

        let stateAfterFailure = fixture.TryReadVersionState(versionId)
        let outboxCountAfterFailure = fixture.CountVersionPublishedEvents(versionId)
        Assert.Equal(Some "draft", stateAfterFailure)
        Assert.Equal(0L, outboxCountAfterFailure)

        use manifestDoc = JsonDocument.Parse("""{"id":"demo.atomic","version":"1.0.0"}""")
        let manifest = manifestDoc.RootElement.Clone()
        use manifestResponse = upsertManifestWithToken writeToken repoKey versionId manifest ""
        ensureStatus HttpStatusCode.OK manifestResponse |> ignore

        use secondPublishResponse = publishVersionWithToken promoteToken repoKey versionId
        ensureStatus HttpStatusCode.OK secondPublishResponse |> ignore

        let finalState = fixture.TryReadVersionState(versionId)
        let finalOutboxCount = fixture.CountVersionPublishedEvents(versionId)
        Assert.Equal(Some "published", finalState)
        Assert.Equal(1L, finalOutboxCount)

    [<Fact>]
    member _.``P4-02 policy evaluate endpoint enforces authz and records default allow decision`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p402-authz-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p402-authz"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p402-authz-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p402-authz-reader") [| $"repo:{repoKey}:read" |] 60
        let promoteToken, _ = issuePat (makeSubject "p402-authz-promote") [| $"repo:{repoKey}:promote" |] 60

        let packageName = $"policy-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use unauthorizedRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/policy/evaluations")

        unauthorizedRequest.Content <-
            JsonContent.Create(
                { Action = "publish"
                  VersionId = versionId
                  DecisionHint = ""
                  Reason = "default path"
                  PolicyEngineVersion = "policy-test-v1" }
            )

        use unauthorizedResponse = fixture.Client.Send(unauthorizedRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedResponse |> ignore

        use forbiddenResponse =
            evaluatePolicyWithToken readToken repoKey "publish" versionId "" "default path" "policy-test-v1"

        ensureStatus HttpStatusCode.Forbidden forbiddenResponse |> ignore

        use createdResponse =
            evaluatePolicyWithToken promoteToken repoKey "publish" versionId "" "default path" "policy-test-v1"

        let createdBody = ensureStatus HttpStatusCode.Created createdResponse
        use createdDoc = JsonDocument.Parse(createdBody)

        Assert.Equal("allow", createdDoc.RootElement.GetProperty("decision").GetString())
        Assert.Equal("default_allow", createdDoc.RootElement.GetProperty("decisionSource").GetString())
        Assert.False(createdDoc.RootElement.GetProperty("quarantined").GetBoolean())

        let persistedCount = fixture.CountPolicyEvaluations(versionId)
        let persistedDecision = fixture.TryReadLatestPolicyDecision(versionId)
        let quarantineStatus = fixture.TryReadQuarantineStatus(versionId)

        Assert.Equal(1L, persistedCount)
        Assert.Equal(Some "allow", persistedDecision)
        Assert.Equal(None, quarantineStatus)

        use auditResponse = getAuditWithToken adminToken 200
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let policyEvaluatedLogged =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun entry -> entry.GetProperty("action").GetString() = "policy.evaluated")

        Assert.True(policyEvaluatedLogged, "Expected policy.evaluated action in audit log.")

    [<Fact>]
    member _.``P4-02 policy evaluate quarantine decision upserts quarantine item`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p402-quarantine-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p402-quarantine"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p402-quarantine-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p402-quarantine-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"quarantine-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use quarantineResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "promote"
                versionId
                "quarantine"
                "policy engine flagged this version"
                "policy-test-v2"

        let quarantineBody = ensureStatus HttpStatusCode.Created quarantineResponse
        use quarantineDoc = JsonDocument.Parse(quarantineBody)

        Assert.Equal("quarantine", quarantineDoc.RootElement.GetProperty("decision").GetString())
        Assert.Equal("hint_quarantine", quarantineDoc.RootElement.GetProperty("decisionSource").GetString())
        Assert.True(quarantineDoc.RootElement.GetProperty("quarantined").GetBoolean())
        let mutable quarantineIdProp = Unchecked.defaultof<JsonElement>
        Assert.True(quarantineDoc.RootElement.TryGetProperty("quarantineId", &quarantineIdProp))

        let persistedCount = fixture.CountPolicyEvaluations(versionId)
        let persistedDecision = fixture.TryReadLatestPolicyDecision(versionId)
        let quarantineStatus = fixture.TryReadQuarantineStatus(versionId)

        Assert.Equal(1L, persistedCount)
        Assert.Equal(Some "quarantine", persistedDecision)
        Assert.Equal(Some "quarantined", quarantineStatus)

    [<Fact>]
    member _.``P4-03 quarantine APIs enforce authz and support list/get/release`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p403-authz-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p403-authz"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p403-authz-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p403-authz-reader") [| $"repo:{repoKey}:read" |] 60
        let promoteToken, _ = issuePat (makeSubject "p403-authz-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"quarantine-release-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use quarantineEvalResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "publish"
                versionId
                "quarantine"
                "release-flow seed"
                "policy-test-v3"

        let quarantineEvalBody = ensureStatus HttpStatusCode.Created quarantineEvalResponse
        use quarantineEvalDoc = JsonDocument.Parse(quarantineEvalBody)
        let quarantineId = quarantineEvalDoc.RootElement.GetProperty("quarantineId").GetGuid()

        use unauthorizedListRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}/quarantine")
        use unauthorizedListResponse = fixture.Client.Send(unauthorizedListRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedListResponse |> ignore

        use forbiddenListResponse = listQuarantineWithToken readToken repoKey None
        ensureStatus HttpStatusCode.Forbidden forbiddenListResponse |> ignore

        use listResponse = listQuarantineWithToken promoteToken repoKey (Some "quarantined")
        let listBody = ensureStatus HttpStatusCode.OK listResponse
        use listDoc = JsonDocument.Parse(listBody)

        let listed =
            listDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun item -> item.GetProperty("quarantineId").GetGuid() = quarantineId)

        Assert.True(listed, "Expected quarantine item in quarantined list response.")

        use getResponse = getQuarantineItemWithToken promoteToken repoKey quarantineId
        let getBody = ensureStatus HttpStatusCode.OK getResponse
        use getDoc = JsonDocument.Parse(getBody)
        Assert.Equal("quarantined", getDoc.RootElement.GetProperty("status").GetString())

        use unauthorizedReleaseRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/quarantine/{quarantineId}/release")

        use unauthorizedReleaseResponse = fixture.Client.Send(unauthorizedReleaseRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedReleaseResponse |> ignore

        use forbiddenReleaseResponse = releaseQuarantineItemWithToken readToken repoKey quarantineId
        ensureStatus HttpStatusCode.Forbidden forbiddenReleaseResponse |> ignore

        use releaseResponse = releaseQuarantineItemWithToken promoteToken repoKey quarantineId
        let releaseBody = ensureStatus HttpStatusCode.OK releaseResponse
        use releaseDoc = JsonDocument.Parse(releaseBody)
        Assert.Equal("released", releaseDoc.RootElement.GetProperty("status").GetString())

        use repeatReleaseResponse = releaseQuarantineItemWithToken promoteToken repoKey quarantineId
        ensureStatus HttpStatusCode.Conflict repeatReleaseResponse |> ignore

        use auditResponse = getAuditWithToken adminToken 300
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let releasedAuditLogged =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun entry -> entry.GetProperty("action").GetString() = "quarantine.released")

        Assert.True(releasedAuditLogged, "Expected quarantine.released action in audit log.")

    [<Fact>]
    member _.``P4-03 reject transitions quarantined item and supports status filter`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p403-reject-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p403-reject"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p403-reject-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p403-reject-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"quarantine-reject-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use quarantineEvalResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "promote"
                versionId
                "quarantine"
                "reject-flow seed"
                "policy-test-v3"

        let quarantineEvalBody = ensureStatus HttpStatusCode.Created quarantineEvalResponse
        use quarantineEvalDoc = JsonDocument.Parse(quarantineEvalBody)
        let quarantineId = quarantineEvalDoc.RootElement.GetProperty("quarantineId").GetGuid()

        use rejectResponse = rejectQuarantineItemWithToken promoteToken repoKey quarantineId
        let rejectBody = ensureStatus HttpStatusCode.OK rejectResponse
        use rejectDoc = JsonDocument.Parse(rejectBody)
        Assert.Equal("rejected", rejectDoc.RootElement.GetProperty("status").GetString())

        use rejectedListResponse = listQuarantineWithToken promoteToken repoKey (Some "rejected")
        let rejectedListBody = ensureStatus HttpStatusCode.OK rejectedListResponse
        use rejectedListDoc = JsonDocument.Parse(rejectedListBody)

        let listedAsRejected =
            rejectedListDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun item ->
                item.GetProperty("quarantineId").GetGuid() = quarantineId
                && item.GetProperty("status").GetString() = "rejected")

        Assert.True(listedAsRejected, "Expected quarantine item in rejected list response.")

        use auditResponse = getAuditWithToken adminToken 300
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let rejectedAuditLogged =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun entry -> entry.GetProperty("action").GetString() = "quarantine.rejected")

        Assert.True(rejectedAuditLogged, "Expected quarantine.rejected action in audit log.")

    [<Fact>]
    member _.``P4-04 outbox sweep enqueues search index job and marks event delivered`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let adminToken, _ = issuePat (makeSubject "p404-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p404-sweep"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p404-writer") [| $"repo:{repoKey}:write" |] 60
        let packageName = $"search-job-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let payloadJson = JsonSerializer.Serialize({| versionId = versionId |})

        let eventId =
            fixture.InsertOutboxEvent("version.published", "package_version", versionId.ToString(), payloadJson)

        let outcome = fixture.RunSearchIndexOutboxSweep(50)
        Assert.Equal(1, outcome.ClaimedCount)
        Assert.Equal(1, outcome.EnqueuedCount)
        Assert.Equal(1, outcome.DeliveredCount)
        Assert.Equal(0, outcome.RequeuedCount)

        let searchJobCount = fixture.CountSearchIndexJobsForVersion(versionId)
        let delivered = fixture.IsOutboxDelivered(eventId)
        Assert.Equal(1L, searchJobCount)
        Assert.True(delivered, "Expected version.published outbox event to be marked delivered.")

        let replayOutcome = fixture.RunSearchIndexOutboxSweep(50)
        Assert.Equal(0, replayOutcome.ClaimedCount)

    [<Fact>]
    member _.``P4-04 outbox sweep requeues malformed version published event`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let baselineJobs = fixture.CountSearchIndexJobsForTenant()
        let malformedEventId = fixture.InsertOutboxEvent("version.published", "package_version", "not-a-guid", "{}")

        let outcome = fixture.RunSearchIndexOutboxSweep(50)
        Assert.Equal(1, outcome.ClaimedCount)
        Assert.Equal(0, outcome.EnqueuedCount)
        Assert.Equal(0, outcome.DeliveredCount)
        Assert.Equal(1, outcome.RequeuedCount)

        let delivered = fixture.IsOutboxDelivered(malformedEventId)
        Assert.False(delivered, "Expected malformed version.published event to remain undelivered.")

        let jobsAfterSweep = fixture.CountSearchIndexJobsForTenant()
        Assert.Equal(baselineJobs, jobsAfterSweep)

    [<Fact>]
    member _.``P4-05 search job sweep completes pending job for published version`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let adminToken, _ = issuePat (makeSubject "p405-complete-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p405-complete"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p405-complete-writer") [| $"repo:{repoKey}:write" |] 60
        let packageName = $"search-complete-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        fixture.PublishVersionForTest(versionId)

        let payloadJson = JsonSerializer.Serialize({| versionId = versionId |})

        fixture.InsertOutboxEvent("version.published", "package_version", versionId.ToString(), payloadJson)
        |> ignore

        fixture.RunSearchIndexOutboxSweep(50) |> ignore

        let outcome = fixture.RunSearchIndexJobSweep(50, 5)
        Assert.Equal(1, outcome.ClaimedCount)
        Assert.Equal(1, outcome.CompletedCount)
        Assert.Equal(0, outcome.FailedCount)

        let jobState = fixture.TryReadSearchIndexJobForVersion(versionId)

        match jobState with
        | None -> failwith "Expected search_index_jobs row for published version."
        | Some(status, attempts, lastError) ->
            Assert.Equal("completed", status)
            Assert.Equal(0, attempts)
            Assert.Equal(None, lastError)

    [<Fact>]
    member _.``P4-05 search job sweep fails unpublished version and honors max attempts`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let adminToken, _ = issuePat (makeSubject "p405-fail-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p405-fail"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p405-fail-writer") [| $"repo:{repoKey}:write" |] 60
        let packageName = $"search-fail-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let payloadJson = JsonSerializer.Serialize({| versionId = versionId |})
        fixture.InsertOutboxEvent("version.published", "package_version", versionId.ToString(), payloadJson)
        |> ignore

        fixture.RunSearchIndexOutboxSweep(50) |> ignore

        let firstOutcome = fixture.RunSearchIndexJobSweep(50, 2)
        Assert.Equal(1, firstOutcome.ClaimedCount)
        Assert.Equal(0, firstOutcome.CompletedCount)
        Assert.Equal(1, firstOutcome.FailedCount)

        fixture.MakeSearchIndexJobAvailableNow(versionId)

        let secondOutcome = fixture.RunSearchIndexJobSweep(50, 2)
        Assert.Equal(1, secondOutcome.ClaimedCount)
        Assert.Equal(0, secondOutcome.CompletedCount)
        Assert.Equal(1, secondOutcome.FailedCount)

        fixture.MakeSearchIndexJobAvailableNow(versionId)

        let thirdOutcome = fixture.RunSearchIndexJobSweep(50, 2)
        Assert.Equal(0, thirdOutcome.ClaimedCount)

        let jobState = fixture.TryReadSearchIndexJobForVersion(versionId)

        match jobState with
        | None -> failwith "Expected search_index_jobs row for unpublished version."
        | Some(status, attempts, lastError) ->
            Assert.Equal("failed", status)
            Assert.Equal(2, attempts)
            Assert.Equal(Some "version_not_published", lastError)

    [<Fact>]
    member _.``P4-06 download blocks quarantined blob and unblocks after release`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p406-release-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p406-release"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p406-release-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p406-release-reader") [| $"repo:{repoKey}:read" |] 60
        let promoteToken, _ = issuePat (makeSubject "p406-release-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"quarantine-release-read-{Guid.NewGuid():N}"
        let payload = Encoding.UTF8.GetBytes($"p406-release-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P4-06 release-path test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey uploadId
        ensureStatus HttpStatusCode.OK commitResponse |> ignore

        fixture.InsertArtifactEntryForVersion(versionId, $"package/{Guid.NewGuid():N}.nupkg", expectedDigest, expectedLength)

        use quarantineEvalResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "promote"
                versionId
                "quarantine"
                "read-path quarantine gate"
                "policy-test-v4"

        let quarantineEvalBody = ensureStatus HttpStatusCode.Created quarantineEvalResponse
        use quarantineEvalDoc = JsonDocument.Parse(quarantineEvalBody)
        let quarantineId = quarantineEvalDoc.RootElement.GetProperty("quarantineId").GetGuid()

        use blockedResponse = downloadBlobWithToken readToken repoKey expectedDigest None
        let blockedBody = ensureStatus HttpStatusCode.Locked blockedResponse
        use blockedDoc = JsonDocument.Parse(blockedBody)
        Assert.Equal("quarantined_blob", blockedDoc.RootElement.GetProperty("error").GetString())

        use releaseResponse = releaseQuarantineItemWithToken promoteToken repoKey quarantineId
        ensureStatus HttpStatusCode.OK releaseResponse |> ignore

        use restoredResponse = downloadBlobWithToken readToken repoKey expectedDigest None
        Assert.True(
            restoredResponse.StatusCode = HttpStatusCode.OK,
            $"Expected HTTP {(int HttpStatusCode.OK)} but got {(int restoredResponse.StatusCode)}."
        )

        let restoredBytes = restoredResponse.Content.ReadAsByteArrayAsync().Result
        Assert.Equal<byte>(payload, restoredBytes)

    [<Fact>]
    member _.``P4-06 download blocks rejected quarantine blob`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p406-reject-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p406-reject"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p406-reject-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p406-reject-reader") [| $"repo:{repoKey}:read" |] 60
        let promoteToken, _ = issuePat (makeSubject "p406-reject-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"quarantine-reject-read-{Guid.NewGuid():N}"
        let payload = Encoding.UTF8.GetBytes($"p406-reject-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P4-06 reject-path test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey uploadId
        ensureStatus HttpStatusCode.OK commitResponse |> ignore

        fixture.InsertArtifactEntryForVersion(versionId, $"package/{Guid.NewGuid():N}.nupkg", expectedDigest, expectedLength)

        use quarantineEvalResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "promote"
                versionId
                "quarantine"
                "read-path reject gate"
                "policy-test-v4"

        let quarantineEvalBody = ensureStatus HttpStatusCode.Created quarantineEvalResponse
        use quarantineEvalDoc = JsonDocument.Parse(quarantineEvalBody)
        let quarantineId = quarantineEvalDoc.RootElement.GetProperty("quarantineId").GetGuid()

        use rejectResponse = rejectQuarantineItemWithToken promoteToken repoKey quarantineId
        ensureStatus HttpStatusCode.OK rejectResponse |> ignore

        use blockedResponse = downloadBlobWithToken readToken repoKey expectedDigest None
        let blockedBody = ensureStatus HttpStatusCode.Locked blockedResponse
        use blockedDoc = JsonDocument.Parse(blockedBody)
        Assert.Equal("quarantined_blob", blockedDoc.RootElement.GetProperty("error").GetString())

    [<Fact>]
    member _.``P4-07 quarantine get and reject endpoints enforce unauthorized forbidden and authorized paths`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p407-authz-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p407-authz"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p407-authz-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p407-authz-reader") [| $"repo:{repoKey}:read" |] 60
        let promoteToken, _ = issuePat (makeSubject "p407-authz-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"p407-authz-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use quarantineEvalResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "publish"
                versionId
                "quarantine"
                "p407 authz seed"
                "policy-test-v4"

        let quarantineEvalBody = ensureStatus HttpStatusCode.Created quarantineEvalResponse
        use quarantineEvalDoc = JsonDocument.Parse(quarantineEvalBody)
        let quarantineId = quarantineEvalDoc.RootElement.GetProperty("quarantineId").GetGuid()

        use unauthorizedGetRequest =
            new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}/quarantine/{quarantineId}")

        use unauthorizedGetResponse = fixture.Client.Send(unauthorizedGetRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedGetResponse |> ignore

        use forbiddenGetResponse = getQuarantineItemWithToken readToken repoKey quarantineId
        ensureStatus HttpStatusCode.Forbidden forbiddenGetResponse |> ignore

        use authorizedGetResponse = getQuarantineItemWithToken promoteToken repoKey quarantineId
        ensureStatus HttpStatusCode.OK authorizedGetResponse |> ignore

        use unauthorizedRejectRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/quarantine/{quarantineId}/reject")

        use unauthorizedRejectResponse = fixture.Client.Send(unauthorizedRejectRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedRejectResponse |> ignore

        use forbiddenRejectResponse = rejectQuarantineItemWithToken readToken repoKey quarantineId
        ensureStatus HttpStatusCode.Forbidden forbiddenRejectResponse |> ignore

        use authorizedRejectResponse = rejectQuarantineItemWithToken promoteToken repoKey quarantineId
        ensureStatus HttpStatusCode.OK authorizedRejectResponse |> ignore

    [<Fact>]
    member _.``P4-07 policy and quarantine mutation audits include deterministic metadata`` () =
        fixture.RequireAvailable()

        let adminSubject = makeSubject "p407-audit-admin"
        let adminToken, _ = issuePat adminSubject [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p407-audit"
        createRepoAsAdmin adminToken repoKey

        let writeSubject = makeSubject "p407-audit-writer"
        let writeToken, _ = issuePat writeSubject [| $"repo:{repoKey}:write" |] 60
        let promoteSubject = makeSubject "p407-audit-promote"
        let promoteToken, _ = issuePat promoteSubject [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"p407-audit-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use quarantineEvalResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "promote"
                versionId
                "quarantine"
                "p407 audit seed"
                "policy-test-v4"

        let quarantineEvalBody = ensureStatus HttpStatusCode.Created quarantineEvalResponse
        use quarantineEvalDoc = JsonDocument.Parse(quarantineEvalBody)
        let evaluationId = quarantineEvalDoc.RootElement.GetProperty("evaluationId").GetInt64()
        let quarantineId = quarantineEvalDoc.RootElement.GetProperty("quarantineId").GetGuid()

        use releaseResponse = releaseQuarantineItemWithToken promoteToken repoKey quarantineId
        ensureStatus HttpStatusCode.OK releaseResponse |> ignore

        use auditResponse = getAuditWithToken adminToken 500
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let policyAuditEntry =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.find (fun entry ->
                entry.GetProperty("action").GetString() = "policy.evaluated"
                && entry.GetProperty("resourceId").GetString() = evaluationId.ToString())

        Assert.Equal("policy_evaluation", policyAuditEntry.GetProperty("resourceType").GetString())
        Assert.Equal(promoteSubject, policyAuditEntry.GetProperty("actor").GetString())

        let policyDetails = policyAuditEntry.GetProperty("details")
        Assert.Equal(repoKey, policyDetails.GetProperty("repoKey").GetString())
        Assert.Equal(versionId.ToString(), policyDetails.GetProperty("versionId").GetString())
        Assert.Equal("promote", policyDetails.GetProperty("action").GetString())
        Assert.Equal("quarantine", policyDetails.GetProperty("decision").GetString())
        Assert.Equal("hint_quarantine", policyDetails.GetProperty("decisionSource").GetString())
        Assert.Equal(quarantineId.ToString(), policyDetails.GetProperty("quarantineId").GetString())

        let quarantineAuditEntry =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.find (fun entry ->
                entry.GetProperty("action").GetString() = "quarantine.released"
                && entry.GetProperty("resourceId").GetString() = quarantineId.ToString())

        Assert.Equal("quarantine_item", quarantineAuditEntry.GetProperty("resourceType").GetString())
        Assert.Equal(promoteSubject, quarantineAuditEntry.GetProperty("actor").GetString())

        let quarantineDetails = quarantineAuditEntry.GetProperty("details")
        Assert.Equal(repoKey, quarantineDetails.GetProperty("repoKey").GetString())
        Assert.Equal(versionId.ToString(), quarantineDetails.GetProperty("versionId").GetString())
        Assert.Equal("released", quarantineDetails.GetProperty("status").GetString())

    [<Fact>]
    member _.``P4-08 policy timeout fails closed for publish and promote actions`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p408-timeout-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p408-timeout"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p408-timeout-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p408-timeout-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"p408-timeout-{Guid.NewGuid():N}"

        use publishDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let publishDraftBody = ensureStatus HttpStatusCode.Created publishDraftResponse
        use publishDraftDoc = JsonDocument.Parse(publishDraftBody)
        let publishVersionId = publishDraftDoc.RootElement.GetProperty("versionId").GetGuid()

        use promoteDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.1"

        let promoteDraftBody = ensureStatus HttpStatusCode.Created promoteDraftResponse
        use promoteDraftDoc = JsonDocument.Parse(promoteDraftBody)
        let promoteVersionId = promoteDraftDoc.RootElement.GetProperty("versionId").GetGuid()

        let assertTimeoutResponse (action: string) (versionId: Guid) =
            use response =
                evaluatePolicyWithToken
                    promoteToken
                    repoKey
                    action
                    versionId
                    "allow"
                    "p408 timeout test"
                    "simulate_timeout"

            let body = ensureStatus HttpStatusCode.ServiceUnavailable response
            use doc = JsonDocument.Parse(body)

            Assert.Equal("policy_timeout", doc.RootElement.GetProperty("error").GetString())
            Assert.Equal(action, doc.RootElement.GetProperty("action").GetString())
            Assert.True(doc.RootElement.GetProperty("failClosed").GetBoolean())
            Assert.True(doc.RootElement.GetProperty("timeoutMs").GetInt32() > 0)

        assertTimeoutResponse "publish" publishVersionId
        assertTimeoutResponse "promote" promoteVersionId

        Assert.Equal(0L, fixture.CountPolicyEvaluations(publishVersionId))
        Assert.Equal(0L, fixture.CountPolicyEvaluations(promoteVersionId))
        Assert.Equal(None, fixture.TryReadLatestPolicyDecision(publishVersionId))
        Assert.Equal(None, fixture.TryReadLatestPolicyDecision(promoteVersionId))
        Assert.Equal(None, fixture.TryReadQuarantineStatus(publishVersionId))
        Assert.Equal(None, fixture.TryReadQuarantineStatus(promoteVersionId))

        use auditResponse = getAuditWithToken adminToken 500
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let timeoutEntries =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.filter (fun entry ->
                entry.GetProperty("action").GetString() = "policy.timeout"
                && entry.GetProperty("details").GetProperty("repoKey").GetString() = repoKey)
            |> Seq.toList

        Assert.True(
            timeoutEntries.Length >= 2,
            $"Expected at least two policy.timeout audit entries for repo {repoKey}."
        )

        let hasPublishEntry =
            timeoutEntries
            |> List.exists (fun entry ->
                let details = entry.GetProperty("details")

                details.GetProperty("versionId").GetString() = publishVersionId.ToString()
                && details.GetProperty("action").GetString() = "publish"
                && details.GetProperty("failClosed").GetString() = "true")

        let hasPromoteEntry =
            timeoutEntries
            |> List.exists (fun entry ->
                let details = entry.GetProperty("details")

                details.GetProperty("versionId").GetString() = promoteVersionId.ToString()
                && details.GetProperty("action").GetString() = "promote"
                && details.GetProperty("failClosed").GetString() = "true")

        Assert.True(hasPublishEntry, "Expected fail-closed policy.timeout audit entry for publish action.")
        Assert.True(hasPromoteEntry, "Expected fail-closed policy.timeout audit entry for promote action.")

    [<Fact>]
    member _.``P4-09 deny decision persists without quarantine side effects`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p409-deny-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p409-deny"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p409-deny-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p409-deny-promote") [| $"repo:{repoKey}:promote" |] 60
        let packageName = $"p409-deny-{Guid.NewGuid():N}"

        use createDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" packageName "1.0.0"

        let createDraftBody = ensureStatus HttpStatusCode.Created createDraftResponse
        use draftDoc = JsonDocument.Parse(createDraftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        use denyResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "promote"
                versionId
                "deny"
                "p409 deny test"
                "policy-test-v5"

        let denyBody = ensureStatus HttpStatusCode.Created denyResponse
        use denyDoc = JsonDocument.Parse(denyBody)
        Assert.Equal("deny", denyDoc.RootElement.GetProperty("decision").GetString())
        Assert.Equal("hint_deny", denyDoc.RootElement.GetProperty("decisionSource").GetString())
        Assert.False(denyDoc.RootElement.GetProperty("quarantined").GetBoolean())

        let mutable quarantineIdProp = Unchecked.defaultof<JsonElement>
        Assert.True(denyDoc.RootElement.TryGetProperty("quarantineId", &quarantineIdProp))
        Assert.Equal(JsonValueKind.Null, quarantineIdProp.ValueKind)

        Assert.Equal(1L, fixture.CountPolicyEvaluations(versionId))
        Assert.Equal(Some "deny", fixture.TryReadLatestPolicyDecision(versionId))
        Assert.Equal(None, fixture.TryReadQuarantineStatus(versionId))

    [<Fact>]
    member _.``P4-09 degraded search pipeline does not block quarantine flow correctness`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let adminToken, _ = issuePat (makeSubject "p409-search-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p409-search"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p409-search-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p409-search-promote") [| $"repo:{repoKey}:promote" |] 60

        let searchPackageName = $"p409-search-fallback-{Guid.NewGuid():N}"

        use searchDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" searchPackageName "1.0.0"

        let searchDraftBody = ensureStatus HttpStatusCode.Created searchDraftResponse
        use searchDraftDoc = JsonDocument.Parse(searchDraftBody)
        let searchVersionId = searchDraftDoc.RootElement.GetProperty("versionId").GetGuid()

        let payloadJson = JsonSerializer.Serialize({| versionId = searchVersionId |})

        fixture.InsertOutboxEvent("version.published", "package_version", searchVersionId.ToString(), payloadJson)
        |> ignore

        let outboxOutcome = fixture.RunSearchIndexOutboxSweep(50)
        Assert.Equal(1, outboxOutcome.ClaimedCount)
        Assert.Equal(1, outboxOutcome.EnqueuedCount)
        Assert.Equal(1, outboxOutcome.DeliveredCount)
        Assert.Equal(0, outboxOutcome.RequeuedCount)

        let jobOutcome = fixture.RunSearchIndexJobSweep(50, 1)
        Assert.Equal(1, jobOutcome.ClaimedCount)
        Assert.Equal(0, jobOutcome.CompletedCount)
        Assert.Equal(1, jobOutcome.FailedCount)

        match fixture.TryReadSearchIndexJobForVersion(searchVersionId) with
        | None -> failwith "Expected failed search job state for unpublished version."
        | Some(status, attempts, lastError) ->
            Assert.Equal("failed", status)
            Assert.Equal(1, attempts)
            Assert.Equal(Some "version_not_published", lastError)

        let malformedEventId = fixture.InsertOutboxEvent("version.published", "package_version", "not-a-guid", "{}")
        let malformedOutcome = fixture.RunSearchIndexOutboxSweep(50)
        Assert.Equal(1, malformedOutcome.ClaimedCount)
        Assert.Equal(0, malformedOutcome.EnqueuedCount)
        Assert.Equal(0, malformedOutcome.DeliveredCount)
        Assert.Equal(1, malformedOutcome.RequeuedCount)
        Assert.False(fixture.IsOutboxDelivered(malformedEventId))

        let policyPackageName = $"p409-policy-{Guid.NewGuid():N}"

        use policyDraftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" policyPackageName "1.0.1"

        let policyDraftBody = ensureStatus HttpStatusCode.Created policyDraftResponse
        use policyDraftDoc = JsonDocument.Parse(policyDraftBody)
        let policyVersionId = policyDraftDoc.RootElement.GetProperty("versionId").GetGuid()

        use quarantineResponse =
            evaluatePolicyWithToken
                promoteToken
                repoKey
                "promote"
                policyVersionId
                "quarantine"
                "p409 degraded-search quarantine check"
                "policy-test-v5"

        let quarantineBody = ensureStatus HttpStatusCode.Created quarantineResponse
        use quarantineDoc = JsonDocument.Parse(quarantineBody)
        let quarantineId = quarantineDoc.RootElement.GetProperty("quarantineId").GetGuid()

        Assert.Equal("quarantine", quarantineDoc.RootElement.GetProperty("decision").GetString())
        Assert.True(quarantineDoc.RootElement.GetProperty("quarantined").GetBoolean())

        use releaseResponse = releaseQuarantineItemWithToken promoteToken repoKey quarantineId
        ensureStatus HttpStatusCode.OK releaseResponse |> ignore

        Assert.Equal(1L, fixture.CountPolicyEvaluations(policyVersionId))
        Assert.Equal(Some "quarantine", fixture.TryReadLatestPolicyDecision(policyVersionId))
        Assert.Equal(Some "released", fixture.TryReadQuarantineStatus(policyVersionId))

        use releasedListResponse = listQuarantineWithToken promoteToken repoKey (Some "released")
        let releasedListBody = ensureStatus HttpStatusCode.OK releasedListResponse
        use releasedListDoc = JsonDocument.Parse(releasedListBody)

        let listedReleased =
            releasedListDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun item ->
                item.GetProperty("quarantineId").GetGuid() = quarantineId
                && item.GetProperty("status").GetString() = "released")

        Assert.True(
            listedReleased,
            "Expected quarantine release flow to remain correct while search pipeline has degraded events/jobs."
        )

    [<Fact>]
    member _.``P4-09 search outbox sweep handles mixed routing and idempotent job upserts`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let adminToken, _ = issuePat (makeSubject "p410-mixed-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p410-mixed"
        createRepoAsAdmin adminToken repoKey
        let writeToken, _ = issuePat (makeSubject "p410-mixed-writer") [| $"repo:{repoKey}:write" |] 60

        use draftAResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" $"p410-a-{Guid.NewGuid():N}" "1.0.0"

        let draftABody = ensureStatus HttpStatusCode.Created draftAResponse
        use draftADoc = JsonDocument.Parse(draftABody)
        let versionIdA = draftADoc.RootElement.GetProperty("versionId").GetGuid()

        use draftBResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" $"p410-b-{Guid.NewGuid():N}" "1.0.0"

        let draftBBody = ensureStatus HttpStatusCode.Created draftBResponse
        use draftBDoc = JsonDocument.Parse(draftBBody)
        let versionIdB = draftBDoc.RootElement.GetProperty("versionId").GetGuid()

        let payloadA = JsonSerializer.Serialize({| versionId = versionIdA |})
        let payloadB = JsonSerializer.Serialize({| versionId = versionIdB |})

        let eventA1 =
            fixture.InsertOutboxEvent("version.published", "package_version", versionIdA.ToString(), payloadA)

        let eventA2 =
            fixture.InsertOutboxEvent("version.published", "package_version", versionIdA.ToString(), payloadA)

        let eventB =
            fixture.InsertOutboxEvent("version.published", "package_version", "not-a-guid", payloadB)

        let malformedEvent =
            fixture.InsertOutboxEvent("version.published", "package_version", "still-not-a-guid", "{}")

        let outcome = fixture.RunSearchIndexOutboxSweep(100)
        Assert.Equal(4, outcome.ClaimedCount)
        Assert.Equal(3, outcome.EnqueuedCount)
        Assert.Equal(3, outcome.DeliveredCount)
        Assert.Equal(1, outcome.RequeuedCount)

        Assert.True(fixture.IsOutboxDelivered(eventA1), "Expected first valid event to be marked delivered.")
        Assert.True(fixture.IsOutboxDelivered(eventA2), "Expected duplicate valid event to be marked delivered.")
        Assert.True(fixture.IsOutboxDelivered(eventB), "Expected payload-fallback event to be marked delivered.")
        Assert.False(fixture.IsOutboxDelivered(malformedEvent), "Expected malformed event to remain undelivered.")

        let queuedJobs = fixture.CountSearchIndexJobsForTenant()
        Assert.Equal(2L, queuedJobs)

        let jobOutcome = fixture.RunSearchIndexJobSweep(100, 1)
        Assert.Equal(2, jobOutcome.ClaimedCount)
        Assert.Equal(0, jobOutcome.CompletedCount)
        Assert.Equal(2, jobOutcome.FailedCount)

        match fixture.TryReadSearchIndexJobForVersion(versionIdA) with
        | None -> failwith "Expected failed search job for versionIdA."
        | Some(status, attempts, lastError) ->
            Assert.Equal("failed", status)
            Assert.Equal(1, attempts)
            Assert.Equal(Some "version_not_published", lastError)

        match fixture.TryReadSearchIndexJobForVersion(versionIdB) with
        | None -> failwith "Expected failed search job for versionIdB."
        | Some(status, attempts, lastError) ->
            Assert.Equal("failed", status)
            Assert.Equal(1, attempts)
            Assert.Equal(Some "version_not_published", lastError)

        fixture.MakeSearchIndexJobAvailableNow(versionIdA)
        fixture.MakeSearchIndexJobAvailableNow(versionIdB)

        let replayOutcome = fixture.RunSearchIndexJobSweep(100, 1)
        Assert.Equal(0, replayOutcome.ClaimedCount)

    [<Fact>]
    member _.``P4-stress quarantine list filters remain consistent under mixed status transitions`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p4stress-quarantine-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p4stress-quarantine"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p4stress-quarantine-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p4stress-quarantine-promote") [| $"repo:{repoKey}:promote" |] 60

        let createDraftVersionId (nameSuffix: string) =
            use draftResponse =
                createDraftVersionWithToken writeToken repoKey "nuget" "" $"p4stress-{nameSuffix}-{Guid.NewGuid():N}" "1.0.0"

            let body = ensureStatus HttpStatusCode.Created draftResponse
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("versionId").GetGuid()

        let createQuarantineItem (versionId: Guid) (reason: string) =
            use response =
                evaluatePolicyWithToken promoteToken repoKey "promote" versionId "quarantine" reason "policy-stress-v1"

            let body = ensureStatus HttpStatusCode.Created response
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("quarantineId").GetGuid()

        let listQuarantineIds (statusFilter: string option) =
            use response = listQuarantineWithToken promoteToken repoKey statusFilter
            let body = ensureStatus HttpStatusCode.OK response
            use doc = JsonDocument.Parse(body)

            doc.RootElement.EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("quarantineId").GetGuid())
            |> Set.ofSeq

        let versionA = createDraftVersionId "release"
        let versionB = createDraftVersionId "reject"
        let versionC = createDraftVersionId "quarantined"

        let quarantineA = createQuarantineItem versionA "p4 stress release flow"
        let quarantineB = createQuarantineItem versionB "p4 stress reject flow"
        let quarantineC = createQuarantineItem versionC "p4 stress stay quarantined flow"

        use releaseResponse = releaseQuarantineItemWithToken promoteToken repoKey quarantineA
        ensureStatus HttpStatusCode.OK releaseResponse |> ignore

        use rejectResponse = rejectQuarantineItemWithToken promoteToken repoKey quarantineB
        ensureStatus HttpStatusCode.OK rejectResponse |> ignore

        let releasedIds = listQuarantineIds (Some "released")
        let rejectedIds = listQuarantineIds (Some "rejected")
        let quarantinedIds = listQuarantineIds (Some "quarantined")
        let allIds = listQuarantineIds None

        let expectedReleasedIds = Set.ofList [ quarantineA ]
        let expectedRejectedIds = Set.ofList [ quarantineB ]
        let expectedQuarantinedIds = Set.ofList [ quarantineC ]
        let expectedAllIds = Set.ofList [ quarantineA; quarantineB; quarantineC ]

        Assert.Equal<Set<Guid>>(expectedReleasedIds, releasedIds)
        Assert.Equal<Set<Guid>>(expectedRejectedIds, rejectedIds)
        Assert.Equal<Set<Guid>>(expectedQuarantinedIds, quarantinedIds)
        Assert.Equal<Set<Guid>>(expectedAllIds, allIds)

    [<Fact>]
    member _.``P4-stress search sweeps honor batch limits across backlog`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let adminToken, _ = issuePat (makeSubject "p4stress-search-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p4stress-search"
        createRepoAsAdmin adminToken repoKey
        let writeToken, _ = issuePat (makeSubject "p4stress-search-writer") [| $"repo:{repoKey}:write" |] 60

        let createDraftVersionId (nameSuffix: string) =
            use draftResponse =
                createDraftVersionWithToken writeToken repoKey "nuget" "" $"p4stress-search-{nameSuffix}-{Guid.NewGuid():N}" "1.0.0"

            let body = ensureStatus HttpStatusCode.Created draftResponse
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("versionId").GetGuid()

        let versionIds =
            [ "a"; "b"; "c"; "d" ]
            |> List.map createDraftVersionId

        for versionId in versionIds do
            let payload = JsonSerializer.Serialize({| versionId = versionId |})
            fixture.InsertOutboxEvent("version.published", "package_version", versionId.ToString(), payload)
            |> ignore

        let firstOutbox = fixture.RunSearchIndexOutboxSweep(2)
        Assert.Equal(2, firstOutbox.ClaimedCount)
        Assert.Equal(2, firstOutbox.EnqueuedCount)
        Assert.Equal(2, firstOutbox.DeliveredCount)
        Assert.Equal(0, firstOutbox.RequeuedCount)
        Assert.Equal(2L, fixture.CountSearchIndexJobsForTenant())

        let secondOutbox = fixture.RunSearchIndexOutboxSweep(2)
        Assert.Equal(2, secondOutbox.ClaimedCount)
        Assert.Equal(2, secondOutbox.EnqueuedCount)
        Assert.Equal(2, secondOutbox.DeliveredCount)
        Assert.Equal(0, secondOutbox.RequeuedCount)
        Assert.Equal(4L, fixture.CountSearchIndexJobsForTenant())

        let thirdOutbox = fixture.RunSearchIndexOutboxSweep(2)
        Assert.Equal(0, thirdOutbox.ClaimedCount)

        let firstJobSweep = fixture.RunSearchIndexJobSweep(3, 2)
        Assert.Equal(3, firstJobSweep.ClaimedCount)
        Assert.Equal(0, firstJobSweep.CompletedCount)
        Assert.Equal(3, firstJobSweep.FailedCount)

        let secondJobSweep = fixture.RunSearchIndexJobSweep(3, 2)
        Assert.Equal(1, secondJobSweep.ClaimedCount)
        Assert.Equal(0, secondJobSweep.CompletedCount)
        Assert.Equal(1, secondJobSweep.FailedCount)

        versionIds |> List.iter fixture.MakeSearchIndexJobAvailableNow

        let thirdJobSweep = fixture.RunSearchIndexJobSweep(3, 2)
        Assert.Equal(3, thirdJobSweep.ClaimedCount)
        Assert.Equal(0, thirdJobSweep.CompletedCount)
        Assert.Equal(3, thirdJobSweep.FailedCount)

        let fourthJobSweep = fixture.RunSearchIndexJobSweep(3, 2)
        Assert.Equal(1, fourthJobSweep.ClaimedCount)
        Assert.Equal(0, fourthJobSweep.CompletedCount)
        Assert.Equal(1, fourthJobSweep.FailedCount)

        versionIds |> List.iter fixture.MakeSearchIndexJobAvailableNow

        let cappedJobSweep = fixture.RunSearchIndexJobSweep(3, 2)
        Assert.Equal(0, cappedJobSweep.ClaimedCount)
        Assert.Equal(0, cappedJobSweep.CompletedCount)
        Assert.Equal(0, cappedJobSweep.FailedCount)

        for versionId in versionIds do
            match fixture.TryReadSearchIndexJobForVersion(versionId) with
            | None -> failwith $"Expected search job for version {versionId}."
            | Some(status, attempts, lastError) ->
                Assert.Equal("failed", status)
                Assert.Equal(2, attempts)
                Assert.Equal(Some "version_not_published", lastError)

    [<Fact>]
    member _.``P4-stress failed search job is deferred by backoff and not immediately reclaimed`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let adminToken, _ = issuePat (makeSubject "p4stress-backoff-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p4stress-backoff"
        createRepoAsAdmin adminToken repoKey
        let writeToken, _ = issuePat (makeSubject "p4stress-backoff-writer") [| $"repo:{repoKey}:write" |] 60

        use draftResponse =
            createDraftVersionWithToken writeToken repoKey "nuget" "" $"p4stress-backoff-{Guid.NewGuid():N}" "1.0.0"

        let draftBody = ensureStatus HttpStatusCode.Created draftResponse
        use draftDoc = JsonDocument.Parse(draftBody)
        let versionId = draftDoc.RootElement.GetProperty("versionId").GetGuid()

        let payload = JsonSerializer.Serialize({| versionId = versionId |})
        fixture.InsertOutboxEvent("version.published", "package_version", versionId.ToString(), payload)
        |> ignore

        let outboxOutcome = fixture.RunSearchIndexOutboxSweep(20)
        Assert.Equal(1, outboxOutcome.ClaimedCount)
        Assert.Equal(1, outboxOutcome.EnqueuedCount)
        Assert.Equal(1, outboxOutcome.DeliveredCount)
        Assert.Equal(0, outboxOutcome.RequeuedCount)

        let firstSweepReferenceUtc = DateTimeOffset.UtcNow
        let firstJobOutcome = fixture.RunSearchIndexJobSweep(20, 3)
        Assert.Equal(1, firstJobOutcome.ClaimedCount)
        Assert.Equal(0, firstJobOutcome.CompletedCount)
        Assert.Equal(1, firstJobOutcome.FailedCount)

        let immediateRetryOutcome = fixture.RunSearchIndexJobSweep(20, 3)
        Assert.Equal(0, immediateRetryOutcome.ClaimedCount)
        Assert.Equal(0, immediateRetryOutcome.CompletedCount)
        Assert.Equal(0, immediateRetryOutcome.FailedCount)

        match fixture.TryReadSearchIndexJobSchedule(versionId) with
        | None -> failwith "Expected search-index job schedule state after failure."
        | Some(status, attempts, availableAtUtc, lastError) ->
            Assert.Equal("failed", status)
            Assert.Equal(1, attempts)
            Assert.Equal(Some "version_not_published", lastError)
            Assert.True(availableAtUtc > firstSweepReferenceUtc.AddSeconds(20.0))
            Assert.True(availableAtUtc < firstSweepReferenceUtc.AddMinutes(2.0))

    [<Fact>]
    member _.``P4-stress outbox sweep ignores non-version-published events under mixed backlog`` () =
        fixture.RequireAvailable()
        fixture.ResetSearchPipelineStateGlobal()

        let adminToken, _ = issuePat (makeSubject "p4stress-noise-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p4stress-noise"
        createRepoAsAdmin adminToken repoKey
        let writeToken, _ = issuePat (makeSubject "p4stress-noise-writer") [| $"repo:{repoKey}:write" |] 60

        let createDraftVersionId (suffix: string) =
            use draftResponse =
                createDraftVersionWithToken writeToken repoKey "nuget" "" $"p4stress-noise-{suffix}-{Guid.NewGuid():N}" "1.0.0"

            let body = ensureStatus HttpStatusCode.Created draftResponse
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("versionId").GetGuid()

        let versionA = createDraftVersionId "a"
        let versionB = createDraftVersionId "b"
        let payloadA = JsonSerializer.Serialize({| versionId = versionA |})
        let payloadB = JsonSerializer.Serialize({| versionId = versionB |})

        let goodEventA =
            fixture.InsertOutboxEvent("version.published", "package_version", versionA.ToString(), payloadA)

        let goodEventB =
            fixture.InsertOutboxEvent("version.published", "package_version", versionB.ToString(), payloadB)

        let noiseEventA =
            fixture.InsertOutboxEvent("repo.updated", "repo", Guid.NewGuid().ToString(), """{"repo":"changed"}""")

        let noiseEventB =
            fixture.InsertOutboxEvent("package.deleted", "package_version", versionA.ToString(), """{"state":"deleted"}""")

        let noiseEventC =
            fixture.InsertOutboxEvent("audit.replayed", "audit_log", Guid.NewGuid().ToString(), """{"ok":true}""")

        let outboxOutcome = fixture.RunSearchIndexOutboxSweep(100)
        Assert.Equal(2, outboxOutcome.ClaimedCount)
        Assert.Equal(2, outboxOutcome.EnqueuedCount)
        Assert.Equal(2, outboxOutcome.DeliveredCount)
        Assert.Equal(0, outboxOutcome.RequeuedCount)

        Assert.True(fixture.IsOutboxDelivered(goodEventA))
        Assert.True(fixture.IsOutboxDelivered(goodEventB))
        Assert.False(fixture.IsOutboxDelivered(noiseEventA))
        Assert.False(fixture.IsOutboxDelivered(noiseEventB))
        Assert.False(fixture.IsOutboxDelivered(noiseEventC))

        Assert.Equal(2L, fixture.CountSearchIndexJobsForTenant())

    [<Fact>]
    member _.``P4-stress quarantine state remains repo-scoped under concurrent repos`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p4stress-repo-admin") [| "repo:*:admin" |] 60
        let repoA = makeRepoKey "p4stress-repo-a"
        let repoB = makeRepoKey "p4stress-repo-b"
        createRepoAsAdmin adminToken repoA
        createRepoAsAdmin adminToken repoB

        let writeA, _ = issuePat (makeSubject "p4stress-repo-write-a") [| $"repo:{repoA}:write" |] 60
        let promoteA, _ = issuePat (makeSubject "p4stress-repo-promote-a") [| $"repo:{repoA}:promote" |] 60
        let writeB, _ = issuePat (makeSubject "p4stress-repo-write-b") [| $"repo:{repoB}:write" |] 60
        let promoteB, _ = issuePat (makeSubject "p4stress-repo-promote-b") [| $"repo:{repoB}:promote" |] 60

        let createDraftVersionId (token: string) (repoKey: string) (nameSuffix: string) =
            use draftResponse =
                createDraftVersionWithToken token repoKey "nuget" "" $"p4stress-repo-{nameSuffix}-{Guid.NewGuid():N}" "1.0.0"

            let body = ensureStatus HttpStatusCode.Created draftResponse
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("versionId").GetGuid()

        let versionA = createDraftVersionId writeA repoA "a"
        let versionB = createDraftVersionId writeB repoB "b"

        let createQuarantineItem (token: string) (repoKey: string) (versionId: Guid) (reason: string) =
            use response =
                evaluatePolicyWithToken token repoKey "promote" versionId "quarantine" reason "policy-stress-v2"

            let body = ensureStatus HttpStatusCode.Created response
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("quarantineId").GetGuid()

        let quarantineA = createQuarantineItem promoteA repoA versionA "repoA quarantine"
        let quarantineB = createQuarantineItem promoteB repoB versionB "repoB quarantine"

        let listIdsForRepo (token: string) (repoKey: string) (statusFilter: string option) =
            use response = listQuarantineWithToken token repoKey statusFilter
            let body = ensureStatus HttpStatusCode.OK response
            use doc = JsonDocument.Parse(body)

            doc.RootElement.EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("quarantineId").GetGuid())
            |> Set.ofSeq

        let initialRepoA = listIdsForRepo promoteA repoA (Some "quarantined")
        let initialRepoB = listIdsForRepo promoteB repoB (Some "quarantined")
        Assert.Equal<Set<Guid>>(Set.ofList [ quarantineA ], initialRepoA)
        Assert.Equal<Set<Guid>>(Set.ofList [ quarantineB ], initialRepoB)

        use crossRepoForbidden = getQuarantineItemWithToken promoteB repoA quarantineA
        ensureStatus HttpStatusCode.Forbidden crossRepoForbidden |> ignore

        use releaseAResponse = releaseQuarantineItemWithToken promoteA repoA quarantineA
        ensureStatus HttpStatusCode.OK releaseAResponse |> ignore

        let releasedRepoA = listIdsForRepo promoteA repoA (Some "released")
        let remainingRepoB = listIdsForRepo promoteB repoB (Some "quarantined")
        Assert.Equal<Set<Guid>>(Set.ofList [ quarantineA ], releasedRepoA)
        Assert.Equal<Set<Guid>>(Set.ofList [ quarantineB ], remainingRepoB)

    [<Fact>]
    member _.``P4-stress quarantined shared digest in one repo does not block download in another repo`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p4stress-shared-admin") [| "repo:*:admin" |] 60
        let repoA = makeRepoKey "p4stress-shared-a"
        let repoB = makeRepoKey "p4stress-shared-b"
        createRepoAsAdmin adminToken repoA
        createRepoAsAdmin adminToken repoB

        let writeA, _ = issuePat (makeSubject "p4stress-shared-write-a") [| $"repo:{repoA}:write" |] 60
        let readA, _ = issuePat (makeSubject "p4stress-shared-read-a") [| $"repo:{repoA}:read" |] 60
        let promoteA, _ = issuePat (makeSubject "p4stress-shared-promote-a") [| $"repo:{repoA}:promote" |] 60

        let writeB, _ = issuePat (makeSubject "p4stress-shared-write-b") [| $"repo:{repoB}:write" |] 60
        let readB, _ = issuePat (makeSubject "p4stress-shared-read-b") [| $"repo:{repoB}:read" |] 60

        let createDraftVersionId (token: string) (repoKey: string) (suffix: string) =
            use draftResponse =
                createDraftVersionWithToken token repoKey "nuget" "" $"p4stress-shared-{suffix}-{Guid.NewGuid():N}" "1.0.0"

            let body = ensureStatus HttpStatusCode.Created draftResponse
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("versionId").GetGuid()

        let versionA = createDraftVersionId writeA repoA "a"
        let versionB = createDraftVersionId writeB repoB "b"

        let payload = Encoding.UTF8.GetBytes($"p4stress-shared-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        let commitSharedDigest (token: string) (repoKey: string) =
            use createResponse = createUploadSessionWithToken token repoKey expectedDigest expectedLength
            let createBody = readResponseBody createResponse

            if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
               || createResponse.StatusCode = HttpStatusCode.NotFound then
                raise (
                    SkipException.ForSkip(
                        $"Skipping P4 shared-digest quarantine isolation test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                    )
                )

            if createResponse.StatusCode = HttpStatusCode.OK then
                use dedupeDoc = JsonDocument.Parse(createBody)
                Assert.True(dedupeDoc.RootElement.GetProperty("deduped").GetBoolean())
                Assert.Equal("committed", dedupeDoc.RootElement.GetProperty("state").GetString())
            else
                Assert.True(
                    createResponse.StatusCode = HttpStatusCode.Created,
                    $"Expected HTTP {(int HttpStatusCode.Created)} or {(int HttpStatusCode.OK)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
                )

                use createDoc = JsonDocument.Parse(createBody)
                let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

                use partResponse = createUploadPartWithToken token repoKey uploadId 1
                let partBody = ensureStatus HttpStatusCode.OK partResponse
                use partDoc = JsonDocument.Parse(partBody)
                let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
                let etag = uploadPartFromPresignedUrl uploadUrl payload

                use completeResponse =
                    completeUploadWithToken
                        token
                        repoKey
                        uploadId
                        [| { PartNumber = 1
                             ETag = etag } |]

                ensureStatus HttpStatusCode.OK completeResponse |> ignore

                use commitResponse = commitUploadWithToken token repoKey uploadId
                ensureStatus HttpStatusCode.OK commitResponse |> ignore

        commitSharedDigest writeA repoA
        commitSharedDigest writeB repoB

        fixture.InsertArtifactEntryForVersion(versionA, $"package/{Guid.NewGuid():N}.nupkg", expectedDigest, expectedLength)
        fixture.InsertArtifactEntryForVersion(versionB, $"package/{Guid.NewGuid():N}.nupkg", expectedDigest, expectedLength)

        use quarantineResponse =
            evaluatePolicyWithToken
                promoteA
                repoA
                "promote"
                versionA
                "quarantine"
                "p4 shared digest repo isolation"
                "policy-stress-v3"

        let quarantineBody = ensureStatus HttpStatusCode.Created quarantineResponse
        use quarantineDoc = JsonDocument.Parse(quarantineBody)
        Assert.True(quarantineDoc.RootElement.GetProperty("quarantined").GetBoolean())

        use blockedA = downloadBlobWithToken readA repoA expectedDigest None
        let blockedBody = ensureStatus HttpStatusCode.Locked blockedA
        use blockedDoc = JsonDocument.Parse(blockedBody)
        Assert.Equal("quarantined_blob", blockedDoc.RootElement.GetProperty("error").GetString())

        use allowedB = downloadBlobWithToken readB repoB expectedDigest None
        Assert.True(
            allowedB.StatusCode = HttpStatusCode.OK,
            $"Expected HTTP {(int HttpStatusCode.OK)} from repo {repoB} but got {(int allowedB.StatusCode)}."
        )

        let allowedBytes = allowedB.Content.ReadAsByteArrayAsync().Result
        Assert.Equal<byte>(payload, allowedBytes)

    [<Fact>]
    member _.``P5-01 tombstone endpoint enforces authz and transitions published version to tombstoned`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p501-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p501-tombstone"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p501-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p501-promote") [| $"repo:{repoKey}:promote" |] 60

        let versionId, _ =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p501-{Guid.NewGuid():N}"

        use unauthorizedRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/packages/versions/{versionId}/tombstone")

        unauthorizedRequest.Content <- JsonContent.Create({ Reason = "phase5 unauthorized"; RetentionDays = 7 })
        use unauthorizedResponse = fixture.Client.Send(unauthorizedRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedResponse |> ignore

        use forbiddenResponse = tombstoneVersionWithToken writeToken repoKey versionId "phase5 forbidden" 7
        ensureStatus HttpStatusCode.Forbidden forbiddenResponse |> ignore

        use successResponse = tombstoneVersionWithToken promoteToken repoKey versionId "phase5 retention test" 7
        let successBody = ensureStatus HttpStatusCode.OK successResponse
        use successDoc = JsonDocument.Parse(successBody)
        Assert.Equal("tombstoned", successDoc.RootElement.GetProperty("state").GetString())
        Assert.False(successDoc.RootElement.GetProperty("idempotent").GetBoolean())

        Assert.Equal(Some "tombstoned", fixture.TryReadVersionState(versionId))
        Assert.Equal(1L, fixture.CountTombstonesForVersion(versionId))

    [<Fact>]
    member _.``P5-01 tombstone endpoint is idempotent on repeated request`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p501-idem-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p501-idem"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p501-idem-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p501-idem-promote") [| $"repo:{repoKey}:promote" |] 60

        let versionId, _ =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p501-idem-{Guid.NewGuid():N}"

        use firstResponse = tombstoneVersionWithToken promoteToken repoKey versionId "phase5 first tombstone" 10
        ensureStatus HttpStatusCode.OK firstResponse |> ignore

        use secondResponse = tombstoneVersionWithToken promoteToken repoKey versionId "phase5 second tombstone" 10
        let secondBody = ensureStatus HttpStatusCode.OK secondResponse
        use secondDoc = JsonDocument.Parse(secondBody)
        Assert.True(secondDoc.RootElement.GetProperty("idempotent").GetBoolean())

    [<Fact>]
    member _.``P5-02 GC dry-run reports orphan candidates without deleting blobs`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p502-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p502-gc"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p502-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p502-promote") [| $"repo:{repoKey}:promote" |] 60

        let versionId, tombstonedDigest =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p502-{Guid.NewGuid():N}"

        use tombstoneResponse = tombstoneVersionWithToken promoteToken repoKey versionId "phase5 gc dry-run" 1
        ensureStatus HttpStatusCode.OK tombstoneResponse |> ignore
        fixture.ExpireTombstoneRetention(versionId)

        let orphanDigest = tokenHashFor $"p502-orphan-{Guid.NewGuid():N}"
        fixture.SeedOrphanBlob(orphanDigest, 321L)

        use gcResponse =
            runGcWithToken
                adminToken
                (Some
                    { DryRun = true
                      RetentionGraceHours = 0
                      BatchSize = 250 })

        let gcBody = ensureStatus HttpStatusCode.OK gcResponse
        use gcDoc = JsonDocument.Parse(gcBody)
        Assert.Equal("dry_run", gcDoc.RootElement.GetProperty("mode").GetString())
        Assert.Equal(0, gcDoc.RootElement.GetProperty("deletedBlobCount").GetInt32())
        Assert.Equal(0, gcDoc.RootElement.GetProperty("deletedVersionCount").GetInt32())

        let candidateBlobCount = gcDoc.RootElement.GetProperty("candidateBlobCount").GetInt32()
        Assert.True(candidateBlobCount >= 1, "Expected at least one GC candidate in dry-run.")
        Assert.Equal(Some "tombstoned", fixture.TryReadVersionState(versionId))
        Assert.True(fixture.BlobExists(tombstonedDigest), "Dry-run must not delete referenced tombstoned blobs.")
        Assert.True(fixture.BlobExists(orphanDigest), "Dry-run must not delete blob rows.")

    [<Fact>]
    member _.``P5-03 GC execute hard-deletes expired tombstoned versions and orphan blobs`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p503-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p503-gc"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p503-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p503-promote") [| $"repo:{repoKey}:promote" |] 60

        let versionId, digest =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p503-{Guid.NewGuid():N}"

        use tombstoneResponse = tombstoneVersionWithToken promoteToken repoKey versionId "phase5 gc execute" 1
        ensureStatus HttpStatusCode.OK tombstoneResponse |> ignore
        fixture.ExpireTombstoneRetention(versionId)

        let orphanDigest = tokenHashFor $"p503-orphan-{Guid.NewGuid():N}"
        fixture.SeedOrphanBlob(orphanDigest, 211L)

        Assert.True(fixture.BlobExists(digest))
        Assert.True(fixture.BlobExists(orphanDigest))

        use gcResponse =
            runGcWithToken
                adminToken
                (Some
                    { DryRun = false
                      RetentionGraceHours = 0
                      BatchSize = 500 })

        let gcBody = ensureStatus HttpStatusCode.OK gcResponse
        use gcDoc = JsonDocument.Parse(gcBody)
        Assert.Equal("execute", gcDoc.RootElement.GetProperty("mode").GetString())

        let deletedVersionCount = gcDoc.RootElement.GetProperty("deletedVersionCount").GetInt32()
        let deletedBlobCount = gcDoc.RootElement.GetProperty("deletedBlobCount").GetInt32()

        Assert.True(deletedVersionCount >= 1, "Expected at least one tombstoned version hard-deleted.")
        Assert.True(deletedBlobCount >= 1, "Expected at least one orphan blob hard-deleted.")

        Assert.Equal(None, fixture.TryReadVersionState(versionId))
        Assert.False(fixture.BlobExists(digest))
        Assert.False(fixture.BlobExists(orphanDigest))

    [<Fact>]
    member _.``P5-04 reconcile endpoint reports blob drift summary`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p504-admin") [| "repo:*:admin" |] 60

        let orphanDigest = tokenHashFor $"p504-orphan-{Guid.NewGuid():N}"
        fixture.SeedOrphanBlob(orphanDigest, 133L)

        use reconcileResponse = reconcileBlobsWithToken adminToken 50
        let reconcileBody = ensureStatus HttpStatusCode.OK reconcileResponse
        use reconcileDoc = JsonDocument.Parse(reconcileBody)

        let orphanBlobCount = reconcileDoc.RootElement.GetProperty("orphanBlobCount").GetInt64()
        Assert.True(orphanBlobCount >= 1L)

        let orphanSamples =
            reconcileDoc.RootElement.GetProperty("orphanBlobSamples").EnumerateArray()
            |> Seq.choose (fun element -> element.GetString() |> Option.ofObj)
            |> Seq.toList

        Assert.Contains(orphanDigest, orphanSamples)

    [<Fact>]
    member _.``P6-01 readiness endpoint reports healthy postgres and object storage dependencies`` () =
        fixture.RequireAvailable()

        use response = fixture.Client.GetAsync("/health/ready").Result
        let body = ensureStatus HttpStatusCode.OK response
        use doc = JsonDocument.Parse(body)
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString())

        let dependencies =
            doc.RootElement.GetProperty("dependencies").EnumerateArray()
            |> Seq.map (fun element ->
                let name = element.GetProperty("name").GetString()
                let healthy = element.GetProperty("healthy").GetBoolean()
                name, healthy)
            |> Seq.toList

        let postgresHealthy =
            dependencies
            |> List.tryFind (fun (name, _) -> name = "postgres")
            |> Option.map snd
            |> Option.defaultValue false

        let objectStorageHealthy =
            dependencies
            |> List.tryFind (fun (name, _) -> name = "object_storage")
            |> Option.map snd
            |> Option.defaultValue false

        Assert.True(postgresHealthy)
        Assert.True(objectStorageHealthy)

    [<Fact>]
    member _.``P6-02 ops summary endpoint enforces authz and emits audit`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p602-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p602-ops"
        createRepoAsAdmin adminToken repoKey
        let promoteToken, _ = issuePat (makeSubject "p602-promote") [| $"repo:{repoKey}:promote" |] 60

        use unauthorizedRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/ops/summary")
        use unauthorizedResponse = fixture.Client.Send(unauthorizedRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedResponse |> ignore

        use forbiddenResponse = opsSummaryWithToken promoteToken
        ensureStatus HttpStatusCode.Forbidden forbiddenResponse |> ignore

        use successResponse = opsSummaryWithToken adminToken
        let successBody = ensureStatus HttpStatusCode.OK successResponse
        use successDoc = JsonDocument.Parse(successBody)

        let pendingOutbox = successDoc.RootElement.GetProperty("pendingOutboxEvents").GetInt64()
        let availableOutbox = successDoc.RootElement.GetProperty("availableOutboxEvents").GetInt64()
        let oldestPendingAge = successDoc.RootElement.GetProperty("oldestPendingOutboxAgeSeconds").GetInt64()
        let failedSearchJobs = successDoc.RootElement.GetProperty("failedSearchJobs").GetInt64()
        let pendingSearchJobs = successDoc.RootElement.GetProperty("pendingSearchJobs").GetInt64()
        let incompleteGcRuns = successDoc.RootElement.GetProperty("incompleteGcRuns").GetInt64()
        let recentPolicyTimeouts24h = successDoc.RootElement.GetProperty("recentPolicyTimeouts24h").GetInt64()

        Assert.True(pendingOutbox >= 0L)
        Assert.True(availableOutbox >= 0L)
        Assert.True(oldestPendingAge >= 0L)
        Assert.True(failedSearchJobs >= 0L)
        Assert.True(pendingSearchJobs >= 0L)
        Assert.True(incompleteGcRuns >= 0L)
        Assert.True(recentPolicyTimeouts24h >= 0L)

        use auditResponse = getAuditWithToken adminToken 200
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let hasOpsAudit =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun entry ->
                entry.GetProperty("action").GetString() = "ops.summary.read")

        Assert.True(hasOpsAudit, "Expected ops.summary.read audit action.")

    [<Fact>]
    member _.``P5-05 admin GC and reconcile endpoints enforce unauthorized, forbidden, and authorized paths`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p505-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p505-authz"
        createRepoAsAdmin adminToken repoKey

        let promoteToken, _ = issuePat (makeSubject "p505-promote") [| $"repo:{repoKey}:promote" |] 60

        use unauthorizedReconcileRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/reconcile/blobs?limit=10")
        use unauthorizedReconcileResponse = fixture.Client.Send(unauthorizedReconcileRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedReconcileResponse |> ignore

        use forbiddenReconcileResponse = reconcileBlobsWithToken promoteToken 10
        ensureStatus HttpStatusCode.Forbidden forbiddenReconcileResponse |> ignore

        use authorizedReconcileResponse = reconcileBlobsWithToken adminToken 10
        ensureStatus HttpStatusCode.OK authorizedReconcileResponse |> ignore

        use unauthorizedGcRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/gc/runs")
        unauthorizedGcRequest.Content <- JsonContent.Create({ DryRun = true; RetentionGraceHours = 0; BatchSize = 100 })
        use unauthorizedGcResponse = fixture.Client.Send(unauthorizedGcRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedGcResponse |> ignore

        use forbiddenGcResponse =
            runGcWithToken
                promoteToken
                (Some
                    { DryRun = true
                      RetentionGraceHours = 0
                      BatchSize = 100 })

        ensureStatus HttpStatusCode.Forbidden forbiddenGcResponse |> ignore

        use authorizedGcResponse =
            runGcWithToken
                adminToken
                (Some
                    { DryRun = true
                      RetentionGraceHours = 0
                      BatchSize = 100 })

        ensureStatus HttpStatusCode.OK authorizedGcResponse |> ignore

    [<Fact>]
    member _.``P5-06 GC request validation rejects invalid retentionGraceHours and batchSize`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p506-admin") [| "repo:*:admin" |] 60

        use invalidRetentionResponse =
            runGcWithToken
                adminToken
                (Some
                    { DryRun = true
                      RetentionGraceHours = -1
                      BatchSize = 100 })

        ensureStatus HttpStatusCode.BadRequest invalidRetentionResponse |> ignore

        use invalidBatchResponse =
            runGcWithToken
                adminToken
                (Some
                    { DryRun = true
                      RetentionGraceHours = 0
                      BatchSize = -5 })

        ensureStatus HttpStatusCode.BadRequest invalidBatchResponse |> ignore

    [<Fact>]
    member _.``P5-07 GC execute deletes expired tombstones while preserving retained tombstones`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p507-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p507-gc"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p507-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p507-promote") [| $"repo:{repoKey}:promote" |] 60

        let expiredVersionId, expiredDigest =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p507-expired-{Guid.NewGuid():N}"

        let retainedVersionId, retainedDigest =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p507-retained-{Guid.NewGuid():N}"

        use expiredTombstoneResponse =
            tombstoneVersionWithToken promoteToken repoKey expiredVersionId "phase5 selective gc expired tombstone" 7

        ensureStatus HttpStatusCode.OK expiredTombstoneResponse |> ignore

        use retainedTombstoneResponse =
            tombstoneVersionWithToken promoteToken repoKey retainedVersionId "phase5 selective gc retained tombstone" 7

        ensureStatus HttpStatusCode.OK retainedTombstoneResponse |> ignore

        fixture.ExpireTombstoneRetention(expiredVersionId)

        let orphanDigest = tokenHashFor $"p507-orphan-{Guid.NewGuid():N}"
        fixture.SeedOrphanBlob(orphanDigest, 377L)

        Assert.True(fixture.BlobExists(expiredDigest))
        Assert.True(fixture.BlobExists(retainedDigest))
        Assert.True(fixture.BlobExists(orphanDigest))

        use gcResponse =
            runGcWithToken
                adminToken
                (Some
                    { DryRun = false
                      RetentionGraceHours = 0
                      BatchSize = 500 })

        let gcBody = ensureStatus HttpStatusCode.OK gcResponse
        use gcDoc = JsonDocument.Parse(gcBody)
        Assert.Equal("execute", gcDoc.RootElement.GetProperty("mode").GetString())

        let deletedVersionCount = gcDoc.RootElement.GetProperty("deletedVersionCount").GetInt32()
        let deletedBlobCount = gcDoc.RootElement.GetProperty("deletedBlobCount").GetInt32()
        Assert.True(deletedVersionCount >= 1, "Expected at least one expired tombstoned version to be deleted.")
        Assert.True(deletedBlobCount >= 2, "Expected at least expired version blob and orphan blob to be deleted.")

        Assert.Equal(None, fixture.TryReadVersionState(expiredVersionId))
        Assert.False(fixture.BlobExists(expiredDigest))

        Assert.Equal(Some "tombstoned", fixture.TryReadVersionState(retainedVersionId))
        Assert.True(fixture.BlobExists(retainedDigest))

        Assert.False(fixture.BlobExists(orphanDigest))

    [<Fact>]
    member _.``P5-stress GC execute honors batch size across multiple runs for expired tombstones`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p5stress-admin") [| "repo:*:admin" |] 60

        let runGcCleanupBatch () =
            use response =
                runGcWithToken
                    adminToken
                    (Some
                        { DryRun = false
                          RetentionGraceHours = 0
                          BatchSize = 5000 })

            let body = ensureStatus HttpStatusCode.OK response
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("deletedVersionCount").GetInt32()

        let mutable cleanupDeletedCount = runGcCleanupBatch ()
        let mutable cleanupIterations = 0

        while cleanupDeletedCount > 0 && cleanupIterations < 20 do
            cleanupIterations <- cleanupIterations + 1
            cleanupDeletedCount <- runGcCleanupBatch ()

        let repoKey = makeRepoKey "p5stress-gc"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p5stress-writer") [| $"repo:{repoKey}:write" |] 60
        let promoteToken, _ = issuePat (makeSubject "p5stress-promote") [| $"repo:{repoKey}:promote" |] 60

        let expiredAId, expiredADigest =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p5stress-expired-a-{Guid.NewGuid():N}"

        let expiredBId, expiredBDigest =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p5stress-expired-b-{Guid.NewGuid():N}"

        let retainedId, retainedDigest =
            createPublishedVersionWithBlob repoKey writeToken promoteToken $"p5stress-retained-{Guid.NewGuid():N}"

        use tombstoneExpiredA =
            tombstoneVersionWithToken promoteToken repoKey expiredAId "p5 stress expired a" 7

        ensureStatus HttpStatusCode.OK tombstoneExpiredA |> ignore

        use tombstoneExpiredB =
            tombstoneVersionWithToken promoteToken repoKey expiredBId "p5 stress expired b" 7

        ensureStatus HttpStatusCode.OK tombstoneExpiredB |> ignore

        use tombstoneRetained =
            tombstoneVersionWithToken promoteToken repoKey retainedId "p5 stress retained" 7

        ensureStatus HttpStatusCode.OK tombstoneRetained |> ignore

        fixture.SetTombstoneRetention(expiredAId, DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero))
        fixture.SetTombstoneRetention(expiredBId, DateTimeOffset(1970, 1, 1, 0, 0, 1, TimeSpan.Zero))

        let runGcBatchOne () =
            use response =
                runGcWithToken
                    adminToken
                    (Some
                        { DryRun = false
                          RetentionGraceHours = 0
                          BatchSize = 1 })

            let body = ensureStatus HttpStatusCode.OK response
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("deletedVersionCount").GetInt32()

        let firstDeletedVersionCount = runGcBatchOne ()
        Assert.Equal(1, firstDeletedVersionCount)

        let secondDeletedVersionCount = runGcBatchOne ()
        Assert.Equal(1, secondDeletedVersionCount)

        let thirdDeletedVersionCount = runGcBatchOne ()
        Assert.Equal(0, thirdDeletedVersionCount)

        Assert.Equal(None, fixture.TryReadVersionState(expiredAId))
        Assert.Equal(None, fixture.TryReadVersionState(expiredBId))
        Assert.Equal(Some "tombstoned", fixture.TryReadVersionState(retainedId))

        Assert.False(fixture.BlobExists(expiredADigest))
        Assert.False(fixture.BlobExists(expiredBDigest))
        Assert.True(fixture.BlobExists(retainedDigest))

    [<Fact>]
    member _.``P5-stress reconcile sample limit and GC blob batch drain are deterministic under orphan volume`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p5stress-reconcile-admin") [| "repo:*:admin" |] 60

        let runGcCleanupBatch () =
            use response =
                runGcWithToken
                    adminToken
                    (Some
                        { DryRun = false
                          RetentionGraceHours = 0
                          BatchSize = 5000 })

            let body = ensureStatus HttpStatusCode.OK response
            use doc = JsonDocument.Parse(body)
            let deletedVersions = doc.RootElement.GetProperty("deletedVersionCount").GetInt32()
            let deletedBlobs = doc.RootElement.GetProperty("deletedBlobCount").GetInt32()
            deletedVersions + deletedBlobs

        let mutable cleanupDeleted = runGcCleanupBatch ()
        let mutable cleanupIterations = 0

        while cleanupDeleted > 0 && cleanupIterations < 20 do
            cleanupIterations <- cleanupIterations + 1
            cleanupDeleted <- runGcCleanupBatch ()

        let orphanDigests =
            [ 1 .. 7 ]
            |> List.map (fun idx ->
                let digest = tokenHashFor $"p5stress-reconcile-orphan-{idx}-{Guid.NewGuid():N}"
                fixture.SeedOrphanBlob(digest, int64 (100 + idx))
                digest)

        use reconcileBeforeResponse = reconcileBlobsWithToken adminToken 3
        let reconcileBeforeBody = ensureStatus HttpStatusCode.OK reconcileBeforeResponse
        use reconcileBeforeDoc = JsonDocument.Parse(reconcileBeforeBody)

        let orphanCountBefore = reconcileBeforeDoc.RootElement.GetProperty("orphanBlobCount").GetInt64()
        let orphanSamplesBefore = reconcileBeforeDoc.RootElement.GetProperty("orphanBlobSamples").EnumerateArray() |> Seq.length
        Assert.True(orphanCountBefore >= int64 orphanDigests.Length)
        Assert.Equal(3, orphanSamplesBefore)

        let runGcBatchThree () =
            use response =
                runGcWithToken
                    adminToken
                    (Some
                        { DryRun = false
                          RetentionGraceHours = 0
                          BatchSize = 3 })

            let body = ensureStatus HttpStatusCode.OK response
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("deletedBlobCount").GetInt32()

        let firstDeletedBlobCount = runGcBatchThree ()
        let secondDeletedBlobCount = runGcBatchThree ()
        let thirdDeletedBlobCount = runGcBatchThree ()
        let fourthDeletedBlobCount = runGcBatchThree ()

        Assert.Equal(3, firstDeletedBlobCount)
        Assert.Equal(3, secondDeletedBlobCount)
        Assert.Equal(1, thirdDeletedBlobCount)
        Assert.Equal(0, fourthDeletedBlobCount)

        for digest in orphanDigests do
            Assert.False(fixture.BlobExists(digest))

    [<Fact>]
    member _.``P5-stress GC retention grace excludes fresh orphan blobs until grace window is reduced`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p5stress-grace-admin") [| "repo:*:admin" |] 60

        let runGcCleanupBatch () =
            use response =
                runGcWithToken
                    adminToken
                    (Some
                        { DryRun = false
                          RetentionGraceHours = 0
                          BatchSize = 5000 })

            let body = ensureStatus HttpStatusCode.OK response
            use doc = JsonDocument.Parse(body)
            let deletedVersions = doc.RootElement.GetProperty("deletedVersionCount").GetInt32()
            let deletedBlobs = doc.RootElement.GetProperty("deletedBlobCount").GetInt32()
            deletedVersions + deletedBlobs

        let mutable cleanupDeleted = runGcCleanupBatch ()
        let mutable cleanupIterations = 0

        while cleanupDeleted > 0 && cleanupIterations < 20 do
            cleanupIterations <- cleanupIterations + 1
            cleanupDeleted <- runGcCleanupBatch ()

        let oldDigest = tokenHashFor $"p5stress-grace-old-{Guid.NewGuid():N}"
        let freshDigest = tokenHashFor $"p5stress-grace-fresh-{Guid.NewGuid():N}"
        fixture.SeedOrphanBlob(oldDigest, 201L)
        fixture.SeedOrphanBlob(freshDigest, 202L)
        fixture.SetBlobCreatedAt(oldDigest, DateTimeOffset.UtcNow.AddHours(-12.0))
        fixture.SetBlobCreatedAt(freshDigest, DateTimeOffset.UtcNow)

        use graceGcResponse =
            runGcWithToken
                adminToken
                (Some
                    { DryRun = false
                      RetentionGraceHours = 6
                      BatchSize = 100 })

        let graceGcBody = ensureStatus HttpStatusCode.OK graceGcResponse
        use graceGcDoc = JsonDocument.Parse(graceGcBody)
        let firstDeletedBlobCount = graceGcDoc.RootElement.GetProperty("deletedBlobCount").GetInt32()
        Assert.True(firstDeletedBlobCount >= 1, "Expected old orphan blob to be eligible under 6-hour grace.")

        Assert.False(fixture.BlobExists(oldDigest))
        Assert.True(fixture.BlobExists(freshDigest))

        use zeroGraceGcResponse =
            runGcWithToken
                adminToken
                (Some
                    { DryRun = false
                      RetentionGraceHours = 0
                      BatchSize = 100 })

        let zeroGraceGcBody = ensureStatus HttpStatusCode.OK zeroGraceGcResponse
        use zeroGraceGcDoc = JsonDocument.Parse(zeroGraceGcBody)
        let secondDeletedBlobCount = zeroGraceGcDoc.RootElement.GetProperty("deletedBlobCount").GetInt32()
        Assert.True(secondDeletedBlobCount >= 1, "Expected fresh orphan blob to be eligible once grace is reduced to zero.")

        Assert.False(fixture.BlobExists(freshDigest))

    [<Fact>]
    member _.``P5-stress GC defaults to dry-run when request body is omitted`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p5stress-default-dryrun-admin") [| "repo:*:admin" |] 60
        let orphanDigest = tokenHashFor $"p5stress-default-dryrun-orphan-{Guid.NewGuid():N}"
        fixture.SeedOrphanBlob(orphanDigest, 345L)

        use gcResponse = runGcWithToken adminToken None
        let gcBody = ensureStatus HttpStatusCode.OK gcResponse
        use gcDoc = JsonDocument.Parse(gcBody)

        Assert.Equal("dry_run", gcDoc.RootElement.GetProperty("mode").GetString())
        Assert.Equal(0, gcDoc.RootElement.GetProperty("deletedBlobCount").GetInt32())
        Assert.Equal(0, gcDoc.RootElement.GetProperty("deletedVersionCount").GetInt32())
        Assert.True(fixture.BlobExists(orphanDigest))

    [<Fact>]
    member _.``P6-stress ops summary outbox counters separate pending and available via deterministic deltas`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p6stress-ops-outbox-admin") [| "repo:*:admin" |] 60

        use baselineResponse = opsSummaryWithToken adminToken
        let baselineBody = ensureStatus HttpStatusCode.OK baselineResponse
        use baselineDoc = JsonDocument.Parse(baselineBody)
        let baselinePendingOutbox = baselineDoc.RootElement.GetProperty("pendingOutboxEvents").GetInt64()
        let baselineAvailableOutbox = baselineDoc.RootElement.GetProperty("availableOutboxEvents").GetInt64()
        let baselineOldestPendingAge = baselineDoc.RootElement.GetProperty("oldestPendingOutboxAgeSeconds").GetInt64()

        let now = DateTimeOffset.UtcNow

        let availableEventId =
            fixture.InsertOutboxEvent("wave6.ops.available", "operations", Guid.NewGuid().ToString(), "{}")

        fixture.SetOutboxEventStateForTest(availableEventId, now.AddMinutes(-2.0), now.AddMinutes(-4.0), None)

        let futureEventId =
            fixture.InsertOutboxEvent("wave6.ops.future", "operations", Guid.NewGuid().ToString(), "{}")

        fixture.SetOutboxEventStateForTest(futureEventId, now.AddMinutes(30.0), now.AddMinutes(-3.0), None)

        let deliveredEventId =
            fixture.InsertOutboxEvent("wave6.ops.delivered", "operations", Guid.NewGuid().ToString(), "{}")

        fixture.SetOutboxEventStateForTest(
            deliveredEventId,
            now.AddMinutes(-1.0),
            now.AddMinutes(-5.0),
            Some(now.AddMinutes(-1.0))
        )

        use afterResponse = opsSummaryWithToken adminToken
        let afterBody = ensureStatus HttpStatusCode.OK afterResponse
        use afterDoc = JsonDocument.Parse(afterBody)
        let pendingOutboxAfter = afterDoc.RootElement.GetProperty("pendingOutboxEvents").GetInt64()
        let availableOutboxAfter = afterDoc.RootElement.GetProperty("availableOutboxEvents").GetInt64()
        let oldestPendingAgeAfter = afterDoc.RootElement.GetProperty("oldestPendingOutboxAgeSeconds").GetInt64()

        Assert.Equal(baselinePendingOutbox + 2L, pendingOutboxAfter)
        Assert.Equal(baselineAvailableOutbox + 1L, availableOutboxAfter)
        Assert.True(oldestPendingAgeAfter >= baselineOldestPendingAge)

    [<Fact>]
    member _.``P6-stress ops summary search job status counters track pending processing and failed deltas`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p6stress-ops-search-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p6stress-ops-search"
        createRepoAsAdmin adminToken repoKey
        let writeToken, _ = issuePat (makeSubject "p6stress-ops-search-writer") [| $"repo:{repoKey}:write" |] 60

        let createDraftVersionId (suffix: string) =
            use response =
                createDraftVersionWithToken
                    writeToken
                    repoKey
                    "nuget"
                    ""
                    $"p6stress-ops-search-{suffix}-{Guid.NewGuid():N}"
                    "1.0.0"

            let body = ensureStatus HttpStatusCode.Created response
            use doc = JsonDocument.Parse(body)
            doc.RootElement.GetProperty("versionId").GetGuid()

        use baselineResponse = opsSummaryWithToken adminToken
        let baselineBody = ensureStatus HttpStatusCode.OK baselineResponse
        use baselineDoc = JsonDocument.Parse(baselineBody)
        let baselinePendingJobs = baselineDoc.RootElement.GetProperty("pendingSearchJobs").GetInt64()
        let baselineFailedJobs = baselineDoc.RootElement.GetProperty("failedSearchJobs").GetInt64()

        let versionPending = createDraftVersionId "pending"
        let versionProcessing = createDraftVersionId "processing"
        let versionFailed = createDraftVersionId "failed"
        let now = DateTimeOffset.UtcNow

        fixture.UpsertSearchIndexJobForVersionForTest(versionPending, "pending", 0, now.AddMinutes(-1.0), None)
        fixture.UpsertSearchIndexJobForVersionForTest(versionProcessing, "processing", 1, now.AddMinutes(-1.0), None)

        fixture.UpsertSearchIndexJobForVersionForTest(
            versionFailed,
            "failed",
            3,
            now.AddMinutes(-1.0),
            Some "wave6 synthetic search failure"
        )

        use afterResponse = opsSummaryWithToken adminToken
        let afterBody = ensureStatus HttpStatusCode.OK afterResponse
        use afterDoc = JsonDocument.Parse(afterBody)
        let pendingJobsAfter = afterDoc.RootElement.GetProperty("pendingSearchJobs").GetInt64()
        let failedJobsAfter = afterDoc.RootElement.GetProperty("failedSearchJobs").GetInt64()

        Assert.Equal(baselinePendingJobs + 2L, pendingJobsAfter)
        Assert.Equal(baselineFailedJobs + 1L, failedJobsAfter)

    [<Fact>]
    member _.``P2-03 upload session create API covers unauthorized, forbidden, and authorized paths`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p203-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p203-upload"
        createRepoAsAdmin adminToken repoKey

        let readToken, _ = issuePat (makeSubject "p203-reader") [| $"repo:{repoKey}:read" |] 60
        let writeToken, _ = issuePat (makeSubject "p203-writer") [| $"repo:{repoKey}:write" |] 60

        let expectedDigest = tokenHashFor (Guid.NewGuid().ToString("N"))
        let expectedLength = 32L

        use unauthorizedRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads")
        unauthorizedRequest.Content <- JsonContent.Create({ ExpectedDigest = expectedDigest; ExpectedLength = expectedLength })
        use unauthorizedResponse = fixture.Client.Send(unauthorizedRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedResponse |> ignore

        use forbiddenResponse = createUploadSessionWithToken readToken repoKey expectedDigest expectedLength
        ensureStatus HttpStatusCode.Forbidden forbiddenResponse |> ignore

        use createdResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createdBody = readResponseBody createdResponse

        if createdResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createdResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 upload session authorized-path assertion: object storage unavailable. Response: {(int createdResponse.StatusCode)} {createdBody}"
                )
            )

        Assert.True(
            createdResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createdResponse.StatusCode)}. Body: {createdBody}"
        )

        use createdDoc = JsonDocument.Parse(createdBody)
        let state = createdDoc.RootElement.GetProperty("state").GetString()
        let deduped = createdDoc.RootElement.GetProperty("deduped").GetBoolean()
        let storageUploadId = createdDoc.RootElement.GetProperty("storageUploadId").GetString()
        let objectStagingKey = createdDoc.RootElement.GetProperty("objectStagingKey").GetString()

        Assert.Equal("initiated", state)
        Assert.False(deduped)
        Assert.False(String.IsNullOrWhiteSpace storageUploadId)
        Assert.False(String.IsNullOrWhiteSpace objectStagingKey)

    [<Fact>]
    member _.``P2-04 upload part URL API issues signed part URL for active session`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p204-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p204-parts"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p204-writer") [| $"repo:{repoKey}:write" |] 60
        let expectedDigest = tokenHashFor (Guid.NewGuid().ToString("N"))
        let expectedLength = 64L

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 upload part URL test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)

        let state = partDoc.RootElement.GetProperty("state").GetString()
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()

        Assert.Equal("parts_uploading", state)
        Assert.False(String.IsNullOrWhiteSpace uploadUrl)

    [<Fact>]
    member _.``P2-04 complete endpoint transitions session to pending_commit`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p204-complete-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p204-complete"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p204-complete-writer") [| $"repo:{repoKey}:write" |] 60
        let expectedDigest = tokenHashFor (Guid.NewGuid().ToString("N"))
        let expectedLength = 32L

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 complete test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()

        let payload = Encoding.UTF8.GetBytes("p204-complete-part")
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        let completeBody = ensureStatus HttpStatusCode.OK completeResponse
        use completeDoc = JsonDocument.Parse(completeBody)
        let state = completeDoc.RootElement.GetProperty("state").GetString()
        Assert.Equal("pending_commit", state)

    [<Fact>]
    member _.``P2-04 abort endpoint transitions active session to aborted`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p204-abort-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p204-abort"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p204-abort-writer") [| $"repo:{repoKey}:write" |] 60
        let expectedDigest = tokenHashFor (Guid.NewGuid().ToString("N"))
        let expectedLength = 16L

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 abort test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use abortResponse = abortUploadWithToken writeToken repoKey uploadId "test_abort"
        let abortBody = ensureStatus HttpStatusCode.OK abortResponse
        use abortDoc = JsonDocument.Parse(abortBody)
        let abortState = abortDoc.RootElement.GetProperty("state").GetString()
        Assert.Equal("aborted", abortState)

        use partAfterAbort = createUploadPartWithToken writeToken repoKey uploadId 1
        ensureStatus HttpStatusCode.Conflict partAfterAbort |> ignore

    [<Fact>]
    member _.``P2-05 commit endpoint verifies digest and length then commits`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p205-commit-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p205-commit"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p205-commit-writer") [| $"repo:{repoKey}:write" |] 60
        let payload = Encoding.UTF8.GetBytes($"p205-commit-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 commit test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey uploadId
        let commitBody = ensureStatus HttpStatusCode.OK commitResponse
        use commitDoc = JsonDocument.Parse(commitBody)
        let state = commitDoc.RootElement.GetProperty("state").GetString()
        let digest = commitDoc.RootElement.GetProperty("digest").GetString()
        let length = commitDoc.RootElement.GetProperty("length").GetInt64()

        Assert.Equal("committed", state)
        Assert.Equal(expectedDigest, digest)
        Assert.Equal(expectedLength, length)

    [<Fact>]
    member _.``P2-05 commit mismatch returns deterministic verification error`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p205-mismatch-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p205-mismatch"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p205-mismatch-writer") [| $"repo:{repoKey}:write" |] 60
        let payload = Encoding.UTF8.GetBytes($"p205-wrong-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor "different-content"
        let expectedLength = int64 payload.Length

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 mismatch test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey uploadId
        let commitBody = ensureStatus HttpStatusCode.Conflict commitResponse
        use commitDoc = JsonDocument.Parse(commitBody)
        let errorCode = commitDoc.RootElement.GetProperty("error").GetString()
        Assert.Equal("upload_verification_failed", errorCode)

    [<Fact>]
    member _.``P2-06 download endpoint supports full and ranged blob reads`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p206-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p206-download"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p206-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p206-reader") [| $"repo:{repoKey}:read" |] 60
        let payload = Encoding.UTF8.GetBytes($"p206-download-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 download test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey uploadId
        ensureStatus HttpStatusCode.OK commitResponse |> ignore

        use fullResponse = downloadBlobWithToken readToken repoKey expectedDigest None
        Assert.True(
            fullResponse.StatusCode = HttpStatusCode.OK,
            $"Expected HTTP {(int HttpStatusCode.OK)} but got {(int fullResponse.StatusCode)}."
        )

        let fullBytes = fullResponse.Content.ReadAsByteArrayAsync().Result
        Assert.Equal<byte>(payload, fullBytes)

        use rangeResponse = downloadBlobWithToken readToken repoKey expectedDigest (Some "bytes=3-8")
        Assert.True(
            rangeResponse.StatusCode = HttpStatusCode.PartialContent,
            $"Expected HTTP {(int HttpStatusCode.PartialContent)} but got {(int rangeResponse.StatusCode)}."
        )

        let rangeBytes = rangeResponse.Content.ReadAsByteArrayAsync().Result
        let expectedRange = payload.[3..8]
        Assert.Equal<byte>(expectedRange, rangeBytes)

    [<Fact>]
    member _.``P2-06 download only serves blobs committed in the requested repository`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p206-scope-admin") [| "repo:*:admin" |] 60
        let sourceRepoKey = makeRepoKey "p206-source"
        let targetRepoKey = makeRepoKey "p206-target"
        createRepoAsAdmin adminToken sourceRepoKey
        createRepoAsAdmin adminToken targetRepoKey

        let writeToken, _ = issuePat (makeSubject "p206-scope-writer") [| $"repo:{sourceRepoKey}:write" |] 60
        let sourceReadToken, _ = issuePat (makeSubject "p206-source-reader") [| $"repo:{sourceRepoKey}:read" |] 60
        let targetReadToken, _ = issuePat (makeSubject "p206-target-reader") [| $"repo:{targetRepoKey}:read" |] 60
        let payload = Encoding.UTF8.GetBytes($"p206-scope-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createResponse = createUploadSessionWithToken writeToken sourceRepoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 repo-scoped download test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken sourceRepoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                sourceRepoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken sourceRepoKey uploadId
        ensureStatus HttpStatusCode.OK commitResponse |> ignore

        use sourceRepoDownload = downloadBlobWithToken sourceReadToken sourceRepoKey expectedDigest None
        ensureStatus HttpStatusCode.OK sourceRepoDownload |> ignore

        use targetRepoDownload = downloadBlobWithToken targetReadToken targetRepoKey expectedDigest None
        ensureStatus HttpStatusCode.NotFound targetRepoDownload |> ignore

    [<Fact>]
    member _.``P2-07 upload mutation actions are written to audit log`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p207-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p207-audit"
        createRepoAsAdmin adminToken repoKey

        let payload = Encoding.UTF8.GetBytes($"p207-audit-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createResponse = createUploadSessionWithToken adminToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2 audit test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created || createResponse.StatusCode = HttpStatusCode.OK,
            $"Expected HTTP 201 or 200 but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use auditResponse = getAuditWithToken adminToken 200
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let hasUploadAudit =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.exists (fun entry -> entry.GetProperty("action").GetString() = "upload.session.created")

        Assert.True(hasUploadAudit, "Expected upload.session.created in audit log.")

    [<Fact>]
    member _.``P2-08e expired upload session rejects lifecycle operations`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p208e-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p208e-expired"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p208e-writer") [| $"repo:{repoKey}:write" |] 60
        let payload = Encoding.UTF8.GetBytes($"p208e-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2-08e expired-session test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()
        fixture.ExpireUploadSession(uploadId)

        use partsResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        ensureStatus HttpStatusCode.Conflict partsResponse |> ignore

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = "expired-etag" } |]

        ensureStatus HttpStatusCode.Conflict completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey uploadId
        ensureStatus HttpStatusCode.Conflict commitResponse |> ignore

    [<Fact>]
    member _.``P2-08f dedupe flow returns committed deduped session on second upload`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p208f-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p208f-dedupe"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p208f-writer") [| $"repo:{repoKey}:write" |] 60
        let payload = Encoding.UTF8.GetBytes($"p208f-dedupe-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use firstCreateResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let firstCreateBody = readResponseBody firstCreateResponse

        if firstCreateResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || firstCreateResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2-08f dedupe test: object storage unavailable. Response: {(int firstCreateResponse.StatusCode)} {firstCreateBody}"
                )
            )

        Assert.True(
            firstCreateResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int firstCreateResponse.StatusCode)}. Body: {firstCreateBody}"
        )

        use firstCreateDoc = JsonDocument.Parse(firstCreateBody)
        let firstUploadId = firstCreateDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey firstUploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                firstUploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey firstUploadId
        ensureStatus HttpStatusCode.OK commitResponse |> ignore

        use secondCreateResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let secondCreateBody = ensureStatus HttpStatusCode.OK secondCreateResponse
        use secondDoc = JsonDocument.Parse(secondCreateBody)

        let deduped = secondDoc.RootElement.GetProperty("deduped").GetBoolean()
        let state = secondDoc.RootElement.GetProperty("state").GetString()

        Assert.True(deduped)
        Assert.Equal("committed", state)

    [<Fact>]
    member _.``P2-08h download rejects invalid range and returns 416 for unsatisfiable range`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p208h-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p208h-range"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p208h-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p208h-reader") [| $"repo:{repoKey}:read" |] 60
        let payload = Encoding.UTF8.GetBytes($"p208h-range-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2-08h range test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey uploadId
        ensureStatus HttpStatusCode.OK commitResponse |> ignore

        use invalidRangeResponse = downloadBlobWithToken readToken repoKey expectedDigest (Some "bytes=abc-def")
        ensureStatus HttpStatusCode.BadRequest invalidRangeResponse |> ignore

        use unsatisfiableRangeResponse = downloadBlobWithToken readToken repoKey expectedDigest (Some "bytes=999999-1000000")
        ensureStatus HttpStatusCode.RequestedRangeNotSatisfiable unsatisfiableRangeResponse |> ignore

    [<Fact>]
    member _.``P2-08g authz rejection matrix covers new upload and download endpoints`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p208g-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p208g-authz"
        createRepoAsAdmin adminToken repoKey

        let writeToken, _ = issuePat (makeSubject "p208g-writer") [| $"repo:{repoKey}:write" |] 60
        let readToken, _ = issuePat (makeSubject "p208g-reader") [| $"repo:{repoKey}:read" |] 60
        let promoteToken, _ = issuePat (makeSubject "p208g-promote") [| $"repo:{repoKey}:promote" |] 60

        let payload = Encoding.UTF8.GetBytes($"p208g-authz-payload-{Guid.NewGuid():N}")
        let expectedDigest = tokenHashFor (Encoding.UTF8.GetString(payload))
        let expectedLength = int64 payload.Length

        use createResponse = createUploadSessionWithToken writeToken repoKey expectedDigest expectedLength
        let createBody = readResponseBody createResponse

        if createResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || createResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2-08g authz test: object storage unavailable. Response: {(int createResponse.StatusCode)} {createBody}"
                )
            )

        Assert.True(
            createResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int createResponse.StatusCode)}. Body: {createBody}"
        )

        use createDoc = JsonDocument.Parse(createBody)
        let uploadId = createDoc.RootElement.GetProperty("uploadId").GetGuid()

        use unauthorizedPartsRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads/{uploadId}/parts")
        unauthorizedPartsRequest.Content <- JsonContent.Create({ PartNumber = 1 })
        use unauthorizedPartsResponse = fixture.Client.Send(unauthorizedPartsRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedPartsResponse |> ignore

        use forbiddenPartsResponse = createUploadPartWithToken readToken repoKey uploadId 1
        ensureStatus HttpStatusCode.Forbidden forbiddenPartsResponse |> ignore

        use unauthorizedCompleteRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads/{uploadId}/complete")
        unauthorizedCompleteRequest.Content <- JsonContent.Create({ Parts = [| { PartNumber = 1; ETag = "etag" } |] })
        use unauthorizedCompleteResponse = fixture.Client.Send(unauthorizedCompleteRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedCompleteResponse |> ignore

        use forbiddenCompleteResponse =
            completeUploadWithToken
                readToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = "etag" } |]

        ensureStatus HttpStatusCode.Forbidden forbiddenCompleteResponse |> ignore

        use unauthorizedAbortRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads/{uploadId}/abort")
        unauthorizedAbortRequest.Content <- JsonContent.Create({ Reason = "no-auth" })
        use unauthorizedAbortResponse = fixture.Client.Send(unauthorizedAbortRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedAbortResponse |> ignore

        use forbiddenAbortResponse = abortUploadWithToken readToken repoKey uploadId "forbidden"
        ensureStatus HttpStatusCode.Forbidden forbiddenAbortResponse |> ignore

        use unauthorizedCommitRequest =
            new HttpRequestMessage(HttpMethod.Post, $"/v1/repos/{repoKey}/uploads/{uploadId}/commit")
        use unauthorizedCommitResponse = fixture.Client.Send(unauthorizedCommitRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedCommitResponse |> ignore

        use forbiddenCommitResponse = commitUploadWithToken readToken repoKey uploadId
        ensureStatus HttpStatusCode.Forbidden forbiddenCommitResponse |> ignore

        use partResponse = createUploadPartWithToken writeToken repoKey uploadId 1
        let partBody = ensureStatus HttpStatusCode.OK partResponse
        use partDoc = JsonDocument.Parse(partBody)
        let uploadUrl = partDoc.RootElement.GetProperty("uploadUrl").GetString()
        let etag = uploadPartFromPresignedUrl uploadUrl payload

        use completeResponse =
            completeUploadWithToken
                writeToken
                repoKey
                uploadId
                [| { PartNumber = 1
                     ETag = etag } |]

        ensureStatus HttpStatusCode.OK completeResponse |> ignore

        use commitResponse = commitUploadWithToken writeToken repoKey uploadId
        ensureStatus HttpStatusCode.OK commitResponse |> ignore

        use unauthorizedDownloadRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/repos/{repoKey}/blobs/{expectedDigest}")
        use unauthorizedDownloadResponse = fixture.Client.Send(unauthorizedDownloadRequest)
        ensureStatus HttpStatusCode.Unauthorized unauthorizedDownloadResponse |> ignore

        use forbiddenDownloadResponse = downloadBlobWithToken promoteToken repoKey expectedDigest None
        ensureStatus HttpStatusCode.Forbidden forbiddenDownloadResponse |> ignore

    [<Fact>]
    member _.``P2-08i audit matrix includes all new upload lifecycle actions`` () =
        fixture.RequireAvailable()

        let adminToken, _ = issuePat (makeSubject "p208i-admin") [| "repo:*:admin" |] 60
        let repoKey = makeRepoKey "p208i-audit-matrix"
        createRepoAsAdmin adminToken repoKey

        // Success flow: create -> parts -> complete -> commit.
        let payloadSuccess = Encoding.UTF8.GetBytes($"p208i-success-{Guid.NewGuid():N}")
        let successDigest = tokenHashFor (Encoding.UTF8.GetString(payloadSuccess))
        let successLength = int64 payloadSuccess.Length

        use successCreateResponse = createUploadSessionWithToken adminToken repoKey successDigest successLength
        let successCreateBody = readResponseBody successCreateResponse

        if successCreateResponse.StatusCode = HttpStatusCode.ServiceUnavailable
           || successCreateResponse.StatusCode = HttpStatusCode.NotFound then
            raise (
                SkipException.ForSkip(
                    $"Skipping P2-08i audit matrix test: object storage unavailable. Response: {(int successCreateResponse.StatusCode)} {successCreateBody}"
                )
            )

        Assert.True(
            successCreateResponse.StatusCode = HttpStatusCode.Created,
            $"Expected HTTP {(int HttpStatusCode.Created)} but got {(int successCreateResponse.StatusCode)}. Body: {successCreateBody}"
        )

        use successCreateDoc = JsonDocument.Parse(successCreateBody)
        let successUploadId = successCreateDoc.RootElement.GetProperty("uploadId").GetGuid()

        use successPartResponse = createUploadPartWithToken adminToken repoKey successUploadId 1
        let successPartBody = ensureStatus HttpStatusCode.OK successPartResponse
        use successPartDoc = JsonDocument.Parse(successPartBody)
        let successUploadUrl = successPartDoc.RootElement.GetProperty("uploadUrl").GetString()
        let successEtag = uploadPartFromPresignedUrl successUploadUrl payloadSuccess

        use successCompleteResponse =
            completeUploadWithToken
                adminToken
                repoKey
                successUploadId
                [| { PartNumber = 1
                     ETag = successEtag } |]

        ensureStatus HttpStatusCode.OK successCompleteResponse |> ignore

        use successCommitResponse = commitUploadWithToken adminToken repoKey successUploadId
        ensureStatus HttpStatusCode.OK successCommitResponse |> ignore

        // Abort flow.
        let payloadAbort = Encoding.UTF8.GetBytes($"p208i-abort-{Guid.NewGuid():N}")
        let abortDigest = tokenHashFor (Encoding.UTF8.GetString(payloadAbort))
        let abortLength = int64 payloadAbort.Length

        use abortCreateResponse = createUploadSessionWithToken adminToken repoKey abortDigest abortLength
        let abortCreateBody = ensureStatus HttpStatusCode.Created abortCreateResponse
        use abortCreateDoc = JsonDocument.Parse(abortCreateBody)
        let abortUploadId = abortCreateDoc.RootElement.GetProperty("uploadId").GetGuid()

        use abortResponse = abortUploadWithToken adminToken repoKey abortUploadId "audit_matrix_abort"
        ensureStatus HttpStatusCode.OK abortResponse |> ignore

        // Verification failure flow.
        let payloadMismatch = Encoding.UTF8.GetBytes($"p208i-mismatch-{Guid.NewGuid():N}")
        let mismatchDigest = tokenHashFor $"different-{Guid.NewGuid():N}"
        let mismatchLength = int64 payloadMismatch.Length

        use mismatchCreateResponse = createUploadSessionWithToken adminToken repoKey mismatchDigest mismatchLength
        let mismatchCreateBody = ensureStatus HttpStatusCode.Created mismatchCreateResponse
        use mismatchCreateDoc = JsonDocument.Parse(mismatchCreateBody)
        let mismatchUploadId = mismatchCreateDoc.RootElement.GetProperty("uploadId").GetGuid()

        use mismatchPartResponse = createUploadPartWithToken adminToken repoKey mismatchUploadId 1
        let mismatchPartBody = ensureStatus HttpStatusCode.OK mismatchPartResponse
        use mismatchPartDoc = JsonDocument.Parse(mismatchPartBody)
        let mismatchUploadUrl = mismatchPartDoc.RootElement.GetProperty("uploadUrl").GetString()
        let mismatchEtag = uploadPartFromPresignedUrl mismatchUploadUrl payloadMismatch

        use mismatchCompleteResponse =
            completeUploadWithToken
                adminToken
                repoKey
                mismatchUploadId
                [| { PartNumber = 1
                     ETag = mismatchEtag } |]

        ensureStatus HttpStatusCode.OK mismatchCompleteResponse |> ignore

        use mismatchCommitResponse = commitUploadWithToken adminToken repoKey mismatchUploadId
        ensureStatus HttpStatusCode.Conflict mismatchCommitResponse |> ignore

        use auditResponse = getAuditWithToken adminToken 500
        let auditBody = ensureStatus HttpStatusCode.OK auditResponse
        use auditDoc = JsonDocument.Parse(auditBody)

        let actions =
            auditDoc.RootElement.EnumerateArray()
            |> Seq.map (fun entry -> entry.GetProperty("action").GetString())
            |> Set.ofSeq

        Assert.True(actions.Contains("upload.session.created"), "Expected upload.session.created in audit log.")
        Assert.True(actions.Contains("upload.part.presigned"), "Expected upload.part.presigned in audit log.")
        Assert.True(actions.Contains("upload.completed"), "Expected upload.completed in audit log.")
        Assert.True(actions.Contains("upload.aborted"), "Expected upload.aborted in audit log.")
        Assert.True(actions.Contains("upload.committed"), "Expected upload.committed in audit log.")
        Assert.True(actions.Contains("upload.commit.verification_failed"), "Expected upload.commit.verification_failed in audit log.")
