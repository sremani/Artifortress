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
