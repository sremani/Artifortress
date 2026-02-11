module Tests

open Artifortress.Domain
open Xunit

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
