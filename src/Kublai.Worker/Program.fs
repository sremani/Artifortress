namespace Kublai.Worker

open System
open System.Text.RegularExpressions
open System.Threading
open Npgsql

type SweepOutcome = {
    ClaimedCount: int
    EnqueuedCount: int
    DeliveredCount: int
    RequeuedCount: int
}

type JobSweepOutcome = {
    ClaimedCount: int
    CompletedCount: int
    FailedCount: int
}

type SearchIndexSourceRecord = {
    TenantId: Guid
    RepoId: Guid
    VersionId: Guid
    RepoKey: string
    PackageType: string
    PackageNamespace: string option
    PackageName: string
    PackageVersion: string
    PublishedAtUtc: DateTimeOffset option
    SearchText: string
}

module SearchIndexOutboxProducer =
    type private BatchedEnqueueResult = {
        EnqueuedCount: int
        DeliveredCount: int
    }

    let private claimOutboxEvents (batchSize: int) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
with picked as (
  select outbox_events.event_id,
         outbox_events.tenant_id,
         outbox_events.aggregate_id,
         outbox_events.payload::text as payload_json
  from outbox_events
  where delivered_at is null
    and event_type = 'version.published'
    and available_at <= now()
    and not exists (
      select 1
      from tenant_search_controls tsc
      where tsc.tenant_id = outbox_events.tenant_id
        and tsc.is_paused = true
    )
  order by occurred_at
  limit @batch_size
  for update skip locked
),
claimed as (
  update outbox_events e
  set delivery_attempts = e.delivery_attempts + 1,
      available_at = now() + interval '30 seconds'
  from picked p
  where e.event_id = p.event_id
  returning p.event_id, p.tenant_id, p.aggregate_id, p.payload_json
)
select claimed.event_id, claimed.tenant_id, claimed.aggregate_id, claimed.payload_json
from claimed;
""",
                conn
            )

        cmd.Parameters.AddWithValue("batch_size", WorkerDbParameters.normalizeBatchSize batchSize) |> ignore

        use reader = cmd.ExecuteReader()
        let events = ResizeArray<WorkerDataShapes.ClaimedOutboxEvent>()

        let rec loop () =
            if reader.Read() then
                events.Add(
                    WorkerDataShapes.createClaimedOutboxEvent
                        (reader.GetGuid(0))
                        (reader.GetGuid(1))
                        (reader.GetString(2))
                        (reader.GetString(3))
                )

                loop ()
            else
                events |> Seq.toList

        loop ()

    let private requeueEvent (eventId: Guid) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
update outbox_events
set available_at = now() + interval '5 minutes'
where event_id = @event_id
  and delivered_at is null;
""",
                conn
            )

        cmd.Parameters.AddWithValue("event_id", eventId) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    let private requeueEvents (eventIds: Guid list) (conn: NpgsqlConnection) =
        if not eventIds.IsEmpty then
            use cmd =
                new NpgsqlCommand(
                    """
update outbox_events
set available_at = now() + interval '5 minutes'
where event_id = any(@event_ids)
  and delivered_at is null;
""",
                    conn
                )

            let eventIdsParam = cmd.Parameters.Add("event_ids", NpgsqlTypes.NpgsqlDbType.Array ||| NpgsqlTypes.NpgsqlDbType.Uuid)
            eventIdsParam.Value <- eventIds |> List.toArray
            cmd.ExecuteNonQuery() |> ignore

    let private enqueueSearchJobsAndMarkDelivered
        (routedEvents: (Guid * Guid * Guid) list)
        (conn: NpgsqlConnection)
        =
        if routedEvents.IsEmpty then
            { EnqueuedCount = 0
              DeliveredCount = 0 }
        else
            let eventIds, tenantIds, versionIds =
                routedEvents
                |> List.fold
                    (fun (eventAcc, tenantAcc, versionAcc) (eventId, tenantId, versionId) ->
                        (eventId :: eventAcc, tenantId :: tenantAcc, versionId :: versionAcc))
                    ([], [], [])

            use tx = conn.BeginTransaction()

            use cmd =
                new NpgsqlCommand(
                    """
with payload(event_id, tenant_id, version_id) as (
  select *
  from unnest(@event_ids, @tenant_ids, @version_ids)
),
jobs_source as (
  select distinct tenant_id, version_id
  from payload
),
upserted as (
  insert into search_index_jobs
    (tenant_id, version_id, status, available_at, attempts, last_error, created_at, updated_at)
  select
    jobs_source.tenant_id,
    jobs_source.version_id,
    'pending',
    now(),
    0,
    null,
    now(),
    now()
  from jobs_source
  on conflict (tenant_id, version_id)
  do update set
    status = 'pending',
    attempts = 0,
    available_at = excluded.available_at,
    updated_at = now(),
    last_error = null
),
delivered as (
  update outbox_events
  set delivered_at = now()
  from payload
  where outbox_events.event_id = payload.event_id
    and outbox_events.delivered_at is null
  returning outbox_events.event_id
)
select
  (select count(*) from payload) as enqueued_count,
  (select count(*) from delivered) as delivered_count;
""",
                    conn,
                    tx
                )

            let eventIdsParam = cmd.Parameters.Add("event_ids", NpgsqlTypes.NpgsqlDbType.Array ||| NpgsqlTypes.NpgsqlDbType.Uuid)
            eventIdsParam.Value <- eventIds |> List.rev |> List.toArray

            let tenantIdsParam = cmd.Parameters.Add("tenant_ids", NpgsqlTypes.NpgsqlDbType.Array ||| NpgsqlTypes.NpgsqlDbType.Uuid)
            tenantIdsParam.Value <- tenantIds |> List.rev |> List.toArray

            let versionIdsParam = cmd.Parameters.Add("version_ids", NpgsqlTypes.NpgsqlDbType.Array ||| NpgsqlTypes.NpgsqlDbType.Uuid)
            versionIdsParam.Value <- versionIds |> List.rev |> List.toArray

            use reader = cmd.ExecuteReader()

            let result =
                if reader.Read() then
                    { EnqueuedCount = reader.GetInt32(0)
                      DeliveredCount = reader.GetInt32(1) }
                else
                    { EnqueuedCount = 0
                      DeliveredCount = 0 }

            reader.Close()

            tx.Commit()
            result

    let runSweep (connectionString: string) (batchSize: int) =
        try
            use conn = new NpgsqlConnection(connectionString)
            conn.Open()

            let claimed = claimOutboxEvents batchSize conn
            let requeuedEventIds =
                claimed
                |> List.choose (fun outboxEvent ->
                    match WorkerOutboxFlow.decideRouting outboxEvent.AggregateId outboxEvent.PayloadJson with
                    | WorkerOutboxFlow.Requeue -> Some outboxEvent.EventId
                    | WorkerOutboxFlow.EnqueueVersion _ -> None)

            let routedEvents =
                claimed
                |> List.choose (fun outboxEvent ->
                    match WorkerOutboxFlow.decideRouting outboxEvent.AggregateId outboxEvent.PayloadJson with
                    | WorkerOutboxFlow.Requeue -> None
                    | WorkerOutboxFlow.EnqueueVersion versionId ->
                        Some(outboxEvent.EventId, outboxEvent.TenantId, versionId))

            requeueEvents requeuedEventIds conn
            let enqueueResult = enqueueSearchJobsAndMarkDelivered routedEvents conn

            Ok
                { ClaimedCount = claimed.Length
                  EnqueuedCount = enqueueResult.EnqueuedCount
                  DeliveredCount = enqueueResult.DeliveredCount
                  RequeuedCount = requeuedEventIds.Length }
        with ex ->
            Error $"search_index_outbox_sweep_failed: {ex.Message}"

module SearchIndexJobProcessor =
    let private claimPendingOrFailedJobs (batchSize: int) (maxAttempts: int) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
with candidate as (
  select search_index_jobs.job_id,
         search_index_jobs.tenant_id,
         search_index_jobs.version_id,
         search_index_jobs.attempts
  from search_index_jobs
  where search_index_jobs.status in ('pending', 'failed')
    and search_index_jobs.available_at <= now()
    and search_index_jobs.attempts < @max_attempts
    and not exists (
      select 1
      from tenant_search_controls tsc
      where tsc.tenant_id = search_index_jobs.tenant_id
        and tsc.is_paused = true
    )
  order by search_index_jobs.available_at, search_index_jobs.created_at
  limit @batch_size
  for update skip locked
),
claimed as (
  update search_index_jobs j
  set status = 'processing',
      updated_at = now()
  from candidate c
  where j.job_id = c.job_id
  returning j.job_id, j.tenant_id, j.version_id, j.attempts
)
select claimed.job_id, claimed.tenant_id, claimed.version_id, claimed.attempts
from claimed;
""",
                conn
            )

        cmd.Parameters.AddWithValue("batch_size", WorkerDbParameters.normalizeBatchSize batchSize) |> ignore
        cmd.Parameters.AddWithValue("max_attempts", WorkerDbParameters.normalizeMaxAttempts maxAttempts) |> ignore

        use reader = cmd.ExecuteReader()
        let jobs = ResizeArray<WorkerDataShapes.ClaimedSearchJob>()

        let rec loop () =
            if reader.Read() then
                jobs.Add(
                    WorkerDataShapes.createClaimedSearchJob
                        (reader.GetGuid(0))
                        (reader.GetGuid(1))
                        (reader.GetGuid(2))
                        (reader.GetInt32(3))
                )

                loop ()
            else
                jobs |> Seq.toList

        loop ()

    let private claimStaleProcessingJobs (batchSize: int) (maxAttempts: int) (leaseSeconds: int) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
with candidate as (
  select search_index_jobs.job_id,
         search_index_jobs.tenant_id,
         search_index_jobs.version_id,
         search_index_jobs.attempts
  from search_index_jobs
  where search_index_jobs.status = 'processing'
    and search_index_jobs.updated_at <= now() - make_interval(secs => @lease_seconds)
    and search_index_jobs.attempts < @max_attempts
    and not exists (
      select 1
      from tenant_search_controls tsc
      where tsc.tenant_id = search_index_jobs.tenant_id
        and tsc.is_paused = true
    )
  order by search_index_jobs.updated_at, search_index_jobs.created_at
  limit @batch_size
  for update skip locked
),
claimed as (
  update search_index_jobs j
  set status = 'processing',
      updated_at = now()
  from candidate c
  where j.job_id = c.job_id
  returning j.job_id, j.tenant_id, j.version_id, j.attempts
)
select claimed.job_id, claimed.tenant_id, claimed.version_id, claimed.attempts
from claimed;
""",
                conn
            )

        cmd.Parameters.AddWithValue("batch_size", WorkerDbParameters.normalizeBatchSize batchSize) |> ignore
        cmd.Parameters.AddWithValue("max_attempts", WorkerDbParameters.normalizeMaxAttempts maxAttempts) |> ignore
        cmd.Parameters.AddWithValue("lease_seconds", max 30 leaseSeconds) |> ignore

        use reader = cmd.ExecuteReader()
        let jobs = ResizeArray<WorkerDataShapes.ClaimedSearchJob>()

        let rec loop () =
            if reader.Read() then
                jobs.Add(
                    WorkerDataShapes.createClaimedSearchJob
                        (reader.GetGuid(0))
                        (reader.GetGuid(1))
                        (reader.GetGuid(2))
                        (reader.GetInt32(3))
                )

                loop ()
            else
                jobs |> Seq.toList

        loop ()

    let private claimJobs (batchSize: int) (maxAttempts: int) (leaseSeconds: int) (conn: NpgsqlConnection) =
        let normalizedBatchSize = WorkerDbParameters.normalizeBatchSize batchSize
        let pendingOrFailed = claimPendingOrFailedJobs normalizedBatchSize maxAttempts conn
        let remaining = normalizedBatchSize - pendingOrFailed.Length

        if remaining <= 0 then
            pendingOrFailed
        else
            let staleProcessing = claimStaleProcessingJobs remaining maxAttempts leaseSeconds conn
            pendingOrFailed @ staleProcessing

    let private tryReadSearchIndexSource (tenantId: Guid) (versionId: Guid) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
select
  pv.tenant_id,
  pv.repo_id,
  pv.version_id,
  r.repo_key::text,
  p.package_type,
  p.namespace,
  p.name,
  pv.version,
  pv.published_at,
  concat_ws(
    ' ',
    r.repo_key::text,
    p.package_type,
    coalesce(p.namespace, ''),
    p.name,
    pv.version,
    coalesce(m.manifest_json::text, '')
  ) as search_text
from package_versions pv
join repos r
  on r.repo_id = pv.repo_id
 and r.tenant_id = pv.tenant_id
join packages p
  on p.package_id = pv.package_id
 and p.tenant_id = pv.tenant_id
left join manifests m on m.version_id = pv.version_id
where pv.tenant_id = @tenant_id
  and pv.version_id = @version_id
  and pv.state = 'published'
limit 1;
""",
                conn
            )

        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore

        use reader = cmd.ExecuteReader()

        if reader.Read() then
            let packageNamespace = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
            let publishedAtUtc = if reader.IsDBNull(8) then None else Some(reader.GetFieldValue<DateTimeOffset>(8))

            Ok(
                Some
                    { TenantId = reader.GetGuid(0)
                      RepoId = reader.GetGuid(1)
                      VersionId = reader.GetGuid(2)
                      RepoKey = reader.GetString(3)
                      PackageType = reader.GetString(4)
                      PackageNamespace = packageNamespace
                      PackageName = reader.GetString(6)
                      PackageVersion = reader.GetString(7)
                      PublishedAtUtc = publishedAtUtc
                      SearchText = reader.GetString(9) }
            )
        else
            Ok None

    let private upsertSearchDocument (source: SearchIndexSourceRecord) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
insert into search_documents
  (tenant_id, repo_id, version_id, repo_key, package_type, package_namespace, package_name, package_version, manifest_json, published_at, search_text, indexed_at, updated_at)
values
  (@tenant_id, @repo_id, @version_id, @repo_key, @package_type, @package_namespace, @package_name, @package_version, null, @published_at, @search_text, now(), now())
on conflict (tenant_id, version_id)
do update set
  repo_id = excluded.repo_id,
  repo_key = excluded.repo_key,
  package_type = excluded.package_type,
  package_namespace = excluded.package_namespace,
  package_name = excluded.package_name,
  package_version = excluded.package_version,
  published_at = excluded.published_at,
  search_text = excluded.search_text,
  indexed_at = now(),
  updated_at = now()
where search_documents.repo_id is distinct from excluded.repo_id
   or search_documents.repo_key is distinct from excluded.repo_key
   or search_documents.package_type is distinct from excluded.package_type
   or search_documents.package_namespace is distinct from excluded.package_namespace
   or search_documents.package_name is distinct from excluded.package_name
   or search_documents.package_version is distinct from excluded.package_version
   or search_documents.published_at is distinct from excluded.published_at
   or search_documents.search_text is distinct from excluded.search_text;
""",
                conn
            )

        cmd.Parameters.AddWithValue("tenant_id", source.TenantId) |> ignore
        cmd.Parameters.AddWithValue("repo_id", source.RepoId) |> ignore
        cmd.Parameters.AddWithValue("version_id", source.VersionId) |> ignore
        cmd.Parameters.AddWithValue("repo_key", source.RepoKey) |> ignore
        cmd.Parameters.AddWithValue("package_type", source.PackageType) |> ignore

        let packageNamespaceParam = cmd.Parameters.Add("package_namespace", NpgsqlTypes.NpgsqlDbType.Text)
        packageNamespaceParam.Value <- (match source.PackageNamespace with | Some value -> box value | None -> box DBNull.Value)

        cmd.Parameters.AddWithValue("package_name", source.PackageName) |> ignore
        cmd.Parameters.AddWithValue("package_version", source.PackageVersion) |> ignore

        let publishedAtParam = cmd.Parameters.Add("published_at", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        publishedAtParam.Value <- (match source.PublishedAtUtc with | Some value -> box value | None -> box DBNull.Value)

        cmd.Parameters.AddWithValue("search_text", source.SearchText) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    let private markCompleted (jobId: Guid) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
update search_index_jobs
set status = 'completed',
    last_error = null,
    updated_at = now()
where job_id = @job_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("job_id", jobId) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    let private markFailed
        (jobId: Guid)
        (attempts: int)
        (availableAtUtc: DateTimeOffset)
        (errorMessage: string)
        (conn: NpgsqlConnection)
        =
        use cmd =
            new NpgsqlCommand(
                """
update search_index_jobs
set status = 'failed',
    attempts = @attempts,
    last_error = @last_error,
    available_at = @available_at,
    updated_at = now()
where job_id = @job_id;
""",
                conn
            )

        cmd.Parameters.AddWithValue("job_id", jobId) |> ignore
        cmd.Parameters.AddWithValue("attempts", attempts) |> ignore
        cmd.Parameters.AddWithValue("last_error", errorMessage) |> ignore
        cmd.Parameters.AddWithValue("available_at", availableAtUtc) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    let runSweep (connectionString: string) (batchSize: int) (maxAttempts: int) (leaseSeconds: int) =
        try
            use conn = new NpgsqlConnection(connectionString)
            conn.Open()

            let claimed = claimJobs batchSize maxAttempts leaseSeconds conn
            let mutable metrics = WorkerSweepMetrics.zeroJobs

            for job in claimed do
                let referenceUtc = DateTimeOffset.UtcNow

                let failJob (errorMessage: string) =
                    let attempts, availableAtUtc = WorkerRetryPolicy.computeFailureSchedule referenceUtc job.Attempts
                    markFailed job.JobId attempts availableAtUtc errorMessage conn
                    metrics <- WorkerSweepMetrics.recordFailed metrics

                match tryReadSearchIndexSource job.TenantId job.VersionId conn with
                | Error _ ->
                    failJob "search_index_source_read_failed"
                | Ok None ->
                    match WorkerJobFlow.decideProcessing referenceUtc job.Attempts false with
                    | WorkerJobFlow.Complete ->
                        failJob "version_not_published"
                    | WorkerJobFlow.Fail(attempts, availableAtUtc, errorMessage) ->
                        markFailed job.JobId attempts availableAtUtc errorMessage conn
                        metrics <- WorkerSweepMetrics.recordFailed metrics
                | Ok(Some source) ->
                    match WorkerJobFlow.decideProcessing referenceUtc job.Attempts true with
                    | WorkerJobFlow.Fail(_, _, _) ->
                        failJob "version_not_published"
                    | WorkerJobFlow.Complete ->
                        try
                            upsertSearchDocument source conn
                            markCompleted job.JobId conn
                            metrics <- WorkerSweepMetrics.recordCompleted metrics
                        with _ ->
                            failJob "search_index_write_failed"

            Ok
                { ClaimedCount = claimed.Length
                  CompletedCount = metrics.CompletedCount
                  FailedCount = metrics.FailedCount }
        with ex ->
            Error $"search_index_job_sweep_failed: {ex.Message}"

module WorkerRuntime =
    let connectionStringFromEnvironment () =
        match Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") with
        | null
        | "" -> "Host=localhost;Port=5432;Username=kublai;Password=kublai;Database=kublai"
        | value -> value

    let pollSecondsFromEnvironment () =
        Environment.GetEnvironmentVariable("Worker__PollSeconds")
        |> WorkerEnvParsing.parsePositiveIntOrDefault 30

    let batchSizeFromEnvironment () =
        Environment.GetEnvironmentVariable("Worker__BatchSize")
        |> WorkerEnvParsing.parsePositiveIntOrDefault 100

    let maxSearchJobAttemptsFromEnvironment () =
        Environment.GetEnvironmentVariable("Worker__SearchJobMaxAttempts")
        |> WorkerEnvParsing.parsePositiveIntOrDefault 5

    let searchJobLeaseSecondsFromEnvironment () =
        Environment.GetEnvironmentVariable("Worker__SearchJobLeaseSeconds")
        |> WorkerEnvParsing.parsePositiveIntOrDefault 300

module Program =
    let private redactSensitiveText (value: string) =
        if String.IsNullOrWhiteSpace value then
            value
        else
            [ ("""(?i)(authorization\s*:\s*bearer\s+)([A-Za-z0-9\-\._~\+/=]+)""", "$1[REDACTED]")
              ("""(?i)(bearer\s+)([A-Za-z0-9\-\._~\+/=]+)""", "$1[REDACTED]")
              ("""(?i)(password\s*=\s*)([^;\s]+)""", "$1[REDACTED]")
              ("""(?i)(secret\s*=\s*)([^;\s]+)""", "$1[REDACTED]")
              ("""(?i)(token\s*=\s*)([^;\s]+)""", "$1[REDACTED]") ]
            |> List.fold (fun current (pattern, replacement) -> Regex.Replace(current, pattern, replacement)) value

    [<EntryPoint>]
    let main args =
        use stopToken = new CancellationTokenSource()

        Console.CancelKeyPress.Add(fun eventArgs ->
            eventArgs.Cancel <- true
            stopToken.Cancel()
        )

        let runOnce = args |> Array.exists (fun arg -> String.Equals(arg, "--once", StringComparison.OrdinalIgnoreCase))
        let connectionString = WorkerRuntime.connectionStringFromEnvironment ()
        let pollSeconds = WorkerRuntime.pollSecondsFromEnvironment ()
        let batchSize = WorkerRuntime.batchSizeFromEnvironment ()
        let maxSearchJobAttempts = WorkerRuntime.maxSearchJobAttemptsFromEnvironment ()
        let searchJobLeaseSeconds = WorkerRuntime.searchJobLeaseSecondsFromEnvironment ()

        printfn "Kublai worker started. Press Ctrl+C to stop."
        printfn
            "worker_config batch_size=%d poll_seconds=%d run_once=%b search_job_max_attempts=%d search_job_lease_seconds=%d"
            batchSize
            pollSeconds
            runOnce
            maxSearchJobAttempts
            searchJobLeaseSeconds

        let runSweepAndLog () =
            match SearchIndexOutboxProducer.runSweep connectionString batchSize with
            | Ok outcome ->
                printfn
                    "worker_search_sweep_utc=%O claimed=%d enqueued=%d delivered=%d requeued=%d"
                    DateTimeOffset.UtcNow
                    outcome.ClaimedCount
                    outcome.EnqueuedCount
                    outcome.DeliveredCount
                    outcome.RequeuedCount
            | Error err ->
                printfn "worker_search_sweep_error_utc=%O error=\"%s\"" DateTimeOffset.UtcNow (redactSensitiveText err)

            match SearchIndexJobProcessor.runSweep connectionString batchSize maxSearchJobAttempts searchJobLeaseSeconds with
            | Ok outcome ->
                printfn
                    "worker_job_sweep_utc=%O claimed=%d completed=%d failed=%d"
                    DateTimeOffset.UtcNow
                    outcome.ClaimedCount
                    outcome.CompletedCount
                    outcome.FailedCount
            | Error err ->
                printfn "worker_job_sweep_error_utc=%O error=\"%s\"" DateTimeOffset.UtcNow (redactSensitiveText err)

        if runOnce then
            runSweepAndLog ()
        else
            while not stopToken.IsCancellationRequested do
                runSweepAndLog ()
                Thread.Sleep(TimeSpan.FromSeconds(float pollSeconds))

        printfn "Kublai worker stopped."
        0
