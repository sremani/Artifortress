module TenantLifecycleTests

open Xunit

[<Fact>]
let ``tenant lifecycle validation normalizes valid offboarding events`` () =
    let request: Program.RecordTenantLifecycleEventRequest =
        { Step = "OFFBOARDING.STARTED"
          Status = "STARTED"
          Subject = " Departing-Admin@Example.COM "
          RepoKey = " Libs-Release "
          Reason = " Contract ended "
          RetentionUntilUtc = "2026-05-28T00:00:00Z" }

    match Program.validateTenantLifecycleEventRequest request with
    | Error err -> failwith err
    | Ok(step, status, subject, repoKey, reason, retentionUntilUtc) ->
        Assert.Equal("offboarding.started", step)
        Assert.Equal("started", status)
        Assert.Equal("departing-admin@example.com", subject)
        Assert.Equal("libs-release", repoKey)
        Assert.Equal("Contract ended", reason)
        Assert.Equal("2026-05-28T00:00:00Z", retentionUntilUtc)

[<Fact>]
let ``tenant lifecycle validation requires reason for offboarding`` () =
    let request: Program.RecordTenantLifecycleEventRequest =
        { Step = "offboarding.started"
          Status = "started"
          Subject = "departing-admin@example.com"
          RepoKey = ""
          Reason = " "
          RetentionUntilUtc = "" }

    match Program.validateTenantLifecycleEventRequest request with
    | Ok _ -> failwith "Expected offboarding lifecycle validation to reject a missing reason."
    | Error err -> Assert.Contains("reason is required", err)

[<Fact>]
let ``tenant lifecycle validation rejects unsupported steps`` () =
    let request: Program.RecordTenantLifecycleEventRequest =
        { Step = "tenant.destroyed"
          Status = "completed"
          Subject = ""
          RepoKey = ""
          Reason = "unsupported test"
          RetentionUntilUtc = "" }

    match Program.validateTenantLifecycleEventRequest request with
    | Ok _ -> failwith "Expected unsupported lifecycle step to be rejected."
    | Error err -> Assert.Contains("step must be one of", err)

[<Fact>]
let ``tenant lifecycle validation defaults empty status to completed`` () =
    let request: Program.RecordTenantLifecycleEventRequest =
        { Step = "tenant.created"
          Status = ""
          Subject = ""
          RepoKey = ""
          Reason = ""
          RetentionUntilUtc = "" }

    match Program.validateTenantLifecycleEventRequest request with
    | Error err -> failwith err
    | Ok(_step, status, _subject, _repoKey, _reason, _retentionUntilUtc) -> Assert.Equal("completed", status)
