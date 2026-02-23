module Tests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Artifortress.Domain
open Xunit

[<Literal>]
let private OidcIssuer = "https://idp.example.com"

[<Literal>]
let private OidcAudience = "artifortress-api"

[<Literal>]
let private OidcSecret = "phase7-oidc-hs256-secret"

[<Literal>]
let private OidcRs256KidA = "phase7-rs256-key-a"

[<Literal>]
let private OidcRs256KidB = "phase7-rs256-key-b"

let private toBase64Url (bytes: byte array) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private issueOidcToken (subject: string) (scopes: string array) (expiresAtUtc: DateTimeOffset) =
    let headerJson = """{"alg":"HS256","typ":"JWT"}"""
    let scopeText = scopes |> String.concat " "
    let payloadJson =
        JsonSerializer.Serialize(
            {| iss = OidcIssuer
               sub = subject
               aud = OidcAudience
               exp = expiresAtUtc.ToUnixTimeSeconds()
               scope = scopeText |}
        )

    let headerSegment = headerJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let payloadSegment = payloadJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let signedPayload = $"{headerSegment}.{payloadSegment}"

    let signatureSegment =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(OidcSecret))
        signedPayload |> Encoding.UTF8.GetBytes |> hmac.ComputeHash |> toBase64Url

    $"{signedPayload}.{signatureSegment}"

let private issueOidcTokenWithGroups (subject: string) (groups: string array) (expiresAtUtc: DateTimeOffset) =
    let headerJson = """{"alg":"HS256","typ":"JWT"}"""
    let payloadJson =
        JsonSerializer.Serialize(
            {| iss = OidcIssuer
               sub = subject
               aud = OidcAudience
               exp = expiresAtUtc.ToUnixTimeSeconds()
               groups = groups |}
        )

    let headerSegment = headerJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let payloadSegment = payloadJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let signedPayload = $"{headerSegment}.{payloadSegment}"

    let signatureSegment =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(OidcSecret))
        signedPayload |> Encoding.UTF8.GetBytes |> hmac.ComputeHash |> toBase64Url

    $"{signedPayload}.{signatureSegment}"

let private issueOidcRs256Token
    (rsaParameters: RSAParameters)
    (kid: string)
    (subject: string)
    (scopes: string array)
    (expiresAtUtc: DateTimeOffset)
    =
    let headerJson = JsonSerializer.Serialize({| alg = "RS256"; typ = "JWT"; kid = kid |})
    let scopeText = scopes |> String.concat " "
    let payloadJson =
        JsonSerializer.Serialize(
            {| iss = OidcIssuer
               sub = subject
               aud = OidcAudience
               exp = expiresAtUtc.ToUnixTimeSeconds()
               scope = scopeText |}
        )

    let headerSegment = headerJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let payloadSegment = payloadJson |> Encoding.UTF8.GetBytes |> toBase64Url
    let signedPayload = $"{headerSegment}.{payloadSegment}"

    let signatureSegment =
        use rsa = RSA.Create()
        rsa.ImportParameters(rsaParameters)

        rsa.SignData(
            Encoding.UTF8.GetBytes(signedPayload),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )
        |> toBase64Url

    $"{signedPayload}.{signatureSegment}"

let private buildRsaJwkJsonFragment (kid: string) (publicParameters: RSAParameters) =
    let n = toBase64Url publicParameters.Modulus
    let e = toBase64Url publicParameters.Exponent
    $"{{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}"

type private StubHttpMessageHandler(responseFactory: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    override _.SendAsync(request: HttpRequestMessage, _cancellationToken: CancellationToken) =
        Task.FromResult(responseFactory request)

[<Fact>]
let ``OIDC token validation accepts HS256 token with matching issuer audience and scopes`` () =
    let config: Program.OidcTokenValidationConfig =
        { Issuer = OidcIssuer
          Audience = OidcAudience
          Hs256SharedSecret = Some OidcSecret
          Rs256SigningKeys = []
          ClaimRoleMappings = []
          RemoteJwksState = None }

    let subject = "oidc-user-01"
    let token = issueOidcToken subject [| "repo:*:admin"; "repo:core-libs:read" |] (DateTimeOffset.UtcNow.AddMinutes(5.0))

    match Program.validateOidcBearerToken config token with
    | Error err -> failwithf "Expected valid OIDC token but got: %s" err
    | Ok principal ->
        Assert.Equal(subject, principal.Subject)

        let scopes = principal.Scopes |> List.map RepoScope.value |> Set.ofList
        Assert.True(Set.contains "repo:*:admin" scopes)
        Assert.True(Set.contains "repo:core-libs:read" scopes)

[<Fact>]
let ``OIDC token validation rejects token with mismatched issuer`` () =
    let config: Program.OidcTokenValidationConfig =
        { Issuer = "https://other-idp.example.com"
          Audience = OidcAudience
          Hs256SharedSecret = Some OidcSecret
          Rs256SigningKeys = []
          ClaimRoleMappings = []
          RemoteJwksState = None }

    let token = issueOidcToken "oidc-user-issuer-mismatch" [| "repo:*:admin" |] (DateTimeOffset.UtcNow.AddMinutes(5.0))

    match Program.validateOidcBearerToken config token with
    | Ok _ -> failwith "Expected issuer mismatch validation failure."
    | Error err -> Assert.Contains("issuer", err)

[<Fact>]
let ``OIDC token validation rejects expired token`` () =
    let config: Program.OidcTokenValidationConfig =
        { Issuer = OidcIssuer
          Audience = OidcAudience
          Hs256SharedSecret = Some OidcSecret
          Rs256SigningKeys = []
          ClaimRoleMappings = []
          RemoteJwksState = None }

    let token = issueOidcToken "oidc-user-expired" [| "repo:*:read" |] (DateTimeOffset.UtcNow.AddMinutes(-5.0))

    match Program.validateOidcBearerToken config token with
    | Ok _ -> failwith "Expected expiration validation failure."
    | Error err -> Assert.Contains("expired", err)

[<Fact>]
let ``OIDC token validation resolves claim-role mappings when scope claim is absent`` () =
    let mappings =
        match Program.parseOidcClaimRoleMappings "groups|af-admins|*|admin" with
        | Ok values -> values
        | Error err -> failwithf "Expected valid mapping parse but got: %s" err

    let config: Program.OidcTokenValidationConfig =
        { Issuer = OidcIssuer
          Audience = OidcAudience
          Hs256SharedSecret = Some OidcSecret
          Rs256SigningKeys = []
          ClaimRoleMappings = mappings
          RemoteJwksState = None }

    let token =
        issueOidcTokenWithGroups "oidc-groups-user" [| "af-admins" |] (DateTimeOffset.UtcNow.AddMinutes(5.0))

    match Program.validateOidcBearerToken config token with
    | Error err -> failwithf "Expected claim-role mapping to grant scope but got: %s" err
    | Ok principal ->
        let scopes = principal.Scopes |> List.map RepoScope.value
        Assert.Contains("repo:*:admin", scopes)

[<Fact>]
let ``OIDC token validation accepts RS256 token against configured JWKS`` () =
    use rsa = RSA.Create(2048)
    let privateParameters = rsa.ExportParameters(true)
    let publicParameters = rsa.ExportParameters(false)

    let jwksJson =
        let keyJson = buildRsaJwkJsonFragment OidcRs256KidA publicParameters
        $"{{\"keys\":[{keyJson}]}}"

    let rs256Keys =
        match Program.parseOidcJwksJson jwksJson with
        | Ok keys -> keys
        | Error err -> failwithf "Expected parseable JWKS but got: %s" err

    let config: Program.OidcTokenValidationConfig =
        { Issuer = OidcIssuer
          Audience = OidcAudience
          Hs256SharedSecret = None
          Rs256SigningKeys = rs256Keys
          ClaimRoleMappings = []
          RemoteJwksState = None }

    let token =
        issueOidcRs256Token
            privateParameters
            OidcRs256KidA
            "oidc-rs256-user"
            [| "repo:*:admin" |]
            (DateTimeOffset.UtcNow.AddMinutes(5.0))

    match Program.validateOidcBearerToken config token with
    | Error err -> failwithf "Expected valid RS256 token but got: %s" err
    | Ok principal -> Assert.Equal("oidc-rs256-user", principal.Subject)

[<Fact>]
let ``OIDC token validation supports RS256 key rotation with matching kid`` () =
    use rsaA = RSA.Create(2048)
    use rsaB = RSA.Create(2048)
    let privateA = rsaA.ExportParameters(true)
    let privateB = rsaB.ExportParameters(true)
    let publicA = rsaA.ExportParameters(false)
    let publicB = rsaB.ExportParameters(false)

    let jwksJson =
        let keyAJson = buildRsaJwkJsonFragment OidcRs256KidA publicA
        let keyBJson = buildRsaJwkJsonFragment OidcRs256KidB publicB
        $"{{\"keys\":[{keyAJson},{keyBJson}]}}"

    let rs256Keys =
        match Program.parseOidcJwksJson jwksJson with
        | Ok keys -> keys
        | Error err -> failwithf "Expected parseable JWKS but got: %s" err

    let config: Program.OidcTokenValidationConfig =
        { Issuer = OidcIssuer
          Audience = OidcAudience
          Hs256SharedSecret = None
          Rs256SigningKeys = rs256Keys
          ClaimRoleMappings = []
          RemoteJwksState = None }

    let tokenA =
        issueOidcRs256Token
            privateA
            OidcRs256KidA
            "oidc-rs256-rotation-a"
            [| "repo:*:read" |]
            (DateTimeOffset.UtcNow.AddMinutes(5.0))

    let tokenB =
        issueOidcRs256Token
            privateB
            OidcRs256KidB
            "oidc-rs256-rotation-b"
            [| "repo:*:write" |]
            (DateTimeOffset.UtcNow.AddMinutes(5.0))

    match Program.validateOidcBearerToken config tokenA with
    | Error err -> failwithf "Expected rotated key A token to validate but got: %s" err
    | Ok principal -> Assert.Equal("oidc-rs256-rotation-a", principal.Subject)

    match Program.validateOidcBearerToken config tokenB with
    | Error err -> failwithf "Expected rotated key B token to validate but got: %s" err
    | Ok principal -> Assert.Equal("oidc-rs256-rotation-b", principal.Subject)

[<Fact>]
let ``OIDC merge signing keys keeps preferred order and deduplicates identical keys`` () =
    use rsaA = RSA.Create(2048)
    use rsaB = RSA.Create(2048)
    let keyAJson = buildRsaJwkJsonFragment "kid-a" (rsaA.ExportParameters(false))
    let keyBJson = buildRsaJwkJsonFragment "kid-b" (rsaB.ExportParameters(false))

    let keyA =
        Program.parseOidcJwksJson $"{{\"keys\":[{keyAJson}]}}"
        |> Result.defaultWith failwith
        |> List.head

    let keyB =
        Program.parseOidcJwksJson $"{{\"keys\":[{keyBJson}]}}"
        |> Result.defaultWith failwith
        |> List.head

    let merged = Program.mergeOidcRs256SigningKeys [ keyB; keyA ] [ keyA ]
    Assert.Equal(2, merged.Length)
    Assert.Equal(Some "kid-b", merged.[0].Kid)
    Assert.Equal(Some "kid-a", merged.[1].Kid)

[<Fact>]
let ``OIDC remote JWKS refresh updates active keys and keeps static fallback keys`` () =
    use rsaA = RSA.Create(2048)
    use rsaB = RSA.Create(2048)
    let staticJwkJson = buildRsaJwkJsonFragment "kid-static" (rsaA.ExportParameters(false))
    let remoteJwkJson = buildRsaJwkJsonFragment "kid-remote" (rsaB.ExportParameters(false))
    let staticJwks = $"{{\"keys\":[{staticJwkJson}]}}"
    let remoteJwks = $"{{\"keys\":[{remoteJwkJson}]}}"

    let staticKeys =
        Program.parseOidcJwksJson staticJwks |> Result.defaultWith failwith

    use httpClient =
        new HttpClient(
            new StubHttpMessageHandler(fun _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(remoteJwks)
                response)
        )

    let remoteState: Program.OidcRemoteJwksState =
        { Endpoint = Uri("https://jwks.example.com/.well-known/jwks.json")
          RefreshInterval = TimeSpan.FromMinutes(5.0)
          RefreshTimeout = TimeSpan.FromSeconds(5.0)
          StaticFallbackKeys = staticKeys
          HttpClient = httpClient
          RefreshLock = new SemaphoreSlim(1, 1)
          ActiveKeys = staticKeys
          LastRefreshAttemptAtUtc = None
          LastRefreshSucceededAtUtc = None
          LastRefreshError = None }

    match Program.refreshOidcRemoteJwks remoteState with
    | Error err -> failwithf "Expected JWKS refresh to succeed but got: %s" err
    | Ok keys ->
        let kids = keys |> List.choose (fun key -> key.Kid) |> Set.ofList
        Assert.True(Set.contains "kid-remote" kids)
        Assert.True(Set.contains "kid-static" kids)
        Assert.True(remoteState.LastRefreshSucceededAtUtc.IsSome)
        Assert.True(remoteState.LastRefreshError.IsNone)

    remoteState.RefreshLock.Dispose()

[<Fact>]
let ``OIDC remote JWKS refresh keeps prior active keys when payload is invalid`` () =
    use rsaA = RSA.Create(2048)
    let staticJwkJson = buildRsaJwkJsonFragment "kid-static" (rsaA.ExportParameters(false))
    let staticJwks = $"{{\"keys\":[{staticJwkJson}]}}"

    let staticKeys =
        Program.parseOidcJwksJson staticJwks |> Result.defaultWith failwith

    use httpClient =
        new HttpClient(
            new StubHttpMessageHandler(fun _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent("{\"keys\":[]}")
                response)
        )

    let remoteState: Program.OidcRemoteJwksState =
        { Endpoint = Uri("https://jwks.example.com/.well-known/jwks.json")
          RefreshInterval = TimeSpan.FromMinutes(5.0)
          RefreshTimeout = TimeSpan.FromSeconds(5.0)
          StaticFallbackKeys = staticKeys
          HttpClient = httpClient
          RefreshLock = new SemaphoreSlim(1, 1)
          ActiveKeys = staticKeys
          LastRefreshAttemptAtUtc = None
          LastRefreshSucceededAtUtc = None
          LastRefreshError = None }

    match Program.refreshOidcRemoteJwks remoteState with
    | Ok _ -> failwith "Expected invalid JWKS payload to fail refresh."
    | Error err -> Assert.Contains("invalid", err, StringComparison.OrdinalIgnoreCase)

    Assert.Equal(staticKeys.Length, remoteState.ActiveKeys.Length)
    Assert.Equal(staticKeys.Head.Kid, remoteState.ActiveKeys.Head.Kid)
    Assert.True(remoteState.LastRefreshError.IsSome)
    remoteState.RefreshLock.Dispose()

[<Fact>]
let ``ServiceName normalizes casing and trims spaces`` () =
    let created = ServiceName.tryCreate "  ArTiFoRtReSs-Api  "
    match created with
    | Ok serviceName -> Assert.Equal("artifortress-api", ServiceName.value serviceName)
    | Error err -> failwithf "Unexpected error: %s" err

[<Fact>]
let ``ServiceName rejects empty input`` () =
    let created = ServiceName.tryCreate "   "
    match created with
    | Ok _ -> failwith "Expected validation error"
    | Error err -> Assert.Equal("Service name cannot be empty.", err)

[<Fact>]
let ``RepoScope parser accepts wildcard admin scope`` () =
    let parsed = RepoScope.tryParse "repo:*:admin"
    match parsed with
    | Ok scope -> Assert.Equal("repo:*:admin", RepoScope.value scope)
    | Error err -> failwithf "Unexpected error: %s" err

[<Fact>]
let ``RepoScope parser rejects malformed scope`` () =
    let parsed = RepoScope.tryParse "invalid-scope"
    match parsed with
    | Ok _ -> failwith "Expected scope parse failure"
    | Error err -> Assert.Contains("Invalid scope", err)

[<Fact>]
let ``RepoRole parser rejects null input`` () =
    let parsed = RepoRole.tryParse null
    match parsed with
    | Ok _ -> failwith "Expected role parse failure"
    | Error err -> Assert.Equal("Role cannot be empty.", err)

[<Fact>]
let ``RepoScope parser rejects null input`` () =
    let parsed = RepoScope.tryParse null
    match parsed with
    | Ok _ -> failwith "Expected scope parse failure"
    | Error err -> Assert.Contains("Invalid scope", err)

[<Fact>]
let ``RepoScope create rejects repo keys containing colon`` () =
    let created = RepoScope.tryCreate "core:libs" RepoRole.Read
    match created with
    | Ok _ -> failwith "Expected repo key validation failure"
    | Error err -> Assert.Equal("Repository key cannot contain ':'.", err)

[<Fact>]
let ``Authorization allows write token to perform read`` () =
    let writeScope =
        match RepoScope.tryParse "repo:core-libs:write" with
        | Ok scope -> scope
        | Error err -> failwithf "Unexpected parse error: %s" err

    let allowed = Authorization.hasRole [ writeScope ] "core-libs" RepoRole.Read
    Assert.True(allowed)

[<Fact>]
let ``Authorization denies promote when only read scope is present`` () =
    let readScope =
        match RepoScope.tryParse "repo:core-libs:read" with
        | Ok scope -> scope
        | Error err -> failwithf "Unexpected parse error: %s" err

    let allowed = Authorization.hasRole [ readScope ] "core-libs" RepoRole.Promote
    Assert.False(allowed)
