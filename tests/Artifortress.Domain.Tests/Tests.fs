module Tests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Artifortress.Domain
open Xunit

[<Literal>]
let private OidcIssuer = "https://idp.example.com"

[<Literal>]
let private OidcAudience = "artifortress-api"

[<Literal>]
let private OidcSecret = "phase7-oidc-hs256-secret"

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

[<Fact>]
let ``OIDC token validation accepts HS256 token with matching issuer audience and scopes`` () =
    let config: Program.OidcTokenValidationConfig =
        { Issuer = OidcIssuer
          Audience = OidcAudience
          Hs256SharedSecret = OidcSecret }

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
          Hs256SharedSecret = OidcSecret }

    let token = issueOidcToken "oidc-user-issuer-mismatch" [| "repo:*:admin" |] (DateTimeOffset.UtcNow.AddMinutes(5.0))

    match Program.validateOidcBearerToken config token with
    | Ok _ -> failwith "Expected issuer mismatch validation failure."
    | Error err -> Assert.Contains("issuer", err)

[<Fact>]
let ``OIDC token validation rejects expired token`` () =
    let config: Program.OidcTokenValidationConfig =
        { Issuer = OidcIssuer
          Audience = OidcAudience
          Hs256SharedSecret = OidcSecret }

    let token = issueOidcToken "oidc-user-expired" [| "repo:*:read" |] (DateTimeOffset.UtcNow.AddMinutes(-5.0))

    match Program.validateOidcBearerToken config token with
    | Ok _ -> failwith "Expected expiration validation failure."
    | Error err -> Assert.Contains("expired", err)

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
