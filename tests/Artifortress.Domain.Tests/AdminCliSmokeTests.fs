module AdminCliSmokeTests

open Artifortress.AdminCli
open Xunit

[<Fact>]
let ``issue PAT command uses bootstrap auth and redacts returned token`` () =
    let plan =
        buildPlan
            [| "--url"
               "https://artifortress.example.com"
               "--bootstrap-token"
               "bootstrap-secret"
               "auth"
               "issue-pat"
               "--subject"
               "platform-admin"
               "--scope"
               "repo:*:admin"
               "--ttl-minutes"
               "60" |]

    match plan with
    | Error err -> failwith err
    | Ok(Single request) ->
        Assert.Equal("POST", request.Method)
        Assert.Equal("/v1/auth/pats", request.PathAndQuery)

        match request.AuthMode with
        | Bootstrap token -> Assert.Equal("bootstrap-secret", token)
        | _ -> failwith "Expected bootstrap token auth."

        Assert.Contains("\"subject\":\"platform-admin\"", request.BodyJson.Value)
        Assert.Contains("\"scopes\":[\"repo:*:admin\"]", request.BodyJson.Value)

        let redacted =
            redactOutput
                """{"tokenId":"8e0d8991-d217-4e7c-8dc8-04d5a28d864e","token":"plain-token-secret","subject":"platform-admin"}"""

        Assert.Contains("\"token\":\"[REDACTED]\"", redacted)
        Assert.DoesNotContain("plain-token-secret", redacted)
        Assert.Contains("tokenId", redacted)
    | Ok _ -> failwith "Expected a single request plan."

[<Fact>]
let ``GC run defaults to dry run unless execute is explicit`` () =
    let plan =
        buildPlan
            [| "--url"
               "https://artifortress.example.com"
               "--token"
               "admin-token"
               "gc"
               "run"
               "--batch-size"
               "50" |]

    match plan with
    | Error err -> failwith err
    | Ok(Single request) ->
        Assert.Equal("POST", request.Method)
        Assert.Equal("/v1/admin/gc/runs", request.PathAndQuery)
        Assert.Contains("\"dryRun\":true", request.BodyJson.Value)
        Assert.Contains("\"batchSize\":50", request.BodyJson.Value)

        match request.AuthMode with
        | Bearer token -> Assert.Equal("admin-token", token)
        | _ -> failwith "Expected bearer token auth."
    | Ok _ -> failwith "Expected a single request plan."

[<Fact>]
let ``preflight combines readiness and ops summary requests`` () =
    let plan =
        buildPlan
            [| "--url"
               "https://artifortress.example.com"
               "--token"
               "admin-token"
               "preflight" |]

    match plan with
    | Error err -> failwith err
    | Ok(Preflight(ready, opsSummary)) ->
        Assert.Equal("GET", ready.Method)
        Assert.Equal("/health/ready", ready.PathAndQuery)
        Assert.Equal(NoAuth, ready.AuthMode)
        Assert.Equal("GET", opsSummary.Method)
        Assert.Equal("/v1/admin/ops/summary", opsSummary.PathAndQuery)
    | Ok _ -> failwith "Expected a preflight request plan."
