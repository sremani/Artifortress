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
