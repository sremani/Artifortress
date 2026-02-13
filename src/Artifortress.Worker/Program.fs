namespace Artifortress.Worker

open System
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
    ManifestJson: string option
}

module SearchIndexOutboxProducer =
    let private claimOutboxEvents (batchSize: int) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
with picked as (
  select event_id, tenant_id, aggregate_id, payload::text as payload_json
  from outbox_events
  where delivered_at is null
    and event_type = 'version.published'
    and available_at <= now()
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
select event_id, tenant_id, aggregate_id, payload_json
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

    let private enqueueSearchJobAndMarkDelivered
        (eventId: Guid)
        (tenantId: Guid)
        (versionId: Guid)
        (conn: NpgsqlConnection)
        =
        use tx = conn.BeginTransaction()

        use jobCmd =
            new NpgsqlCommand(
                """
insert into search_index_jobs
  (tenant_id, version_id, status, available_at, attempts, last_error, created_at, updated_at)
values
  (@tenant_id, @version_id, 'pending', now(), 0, null, now(), now())
on conflict (tenant_id, version_id)
do update set
  status = 'pending',
  attempts = 0,
  available_at = excluded.available_at,
  updated_at = now(),
  last_error = null;
""",
                conn,
                tx
            )

        jobCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        jobCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        jobCmd.ExecuteNonQuery() |> ignore

        use deliveredCmd =
            new NpgsqlCommand(
                """
update outbox_events
set delivered_at = now()
where event_id = @event_id
  and delivered_at is null;
""",
                conn,
                tx
            )

        deliveredCmd.Parameters.AddWithValue("event_id", eventId) |> ignore
        let deliveredRows = deliveredCmd.ExecuteNonQuery()

        tx.Commit()
        deliveredRows > 0

    let runSweep (connectionString: string) (batchSize: int) =
        try
            use conn = new NpgsqlConnection(connectionString)
            conn.Open()

            let claimed = claimOutboxEvents batchSize conn
            let mutable metrics = WorkerSweepMetrics.zeroOutbox

            for outboxEvent in claimed do
                match WorkerOutboxFlow.decideRouting outboxEvent.AggregateId outboxEvent.PayloadJson with
                | WorkerOutboxFlow.Requeue ->
                    requeueEvent outboxEvent.EventId conn
                    metrics <- WorkerSweepMetrics.recordRequeue metrics
                | WorkerOutboxFlow.EnqueueVersion versionId ->
                    let isDelivered =
                        enqueueSearchJobAndMarkDelivered outboxEvent.EventId outboxEvent.TenantId versionId conn

                    metrics <- WorkerSweepMetrics.recordEnqueue isDelivered metrics

            Ok
                { ClaimedCount = claimed.Length
                  EnqueuedCount = metrics.EnqueuedCount
                  DeliveredCount = metrics.DeliveredCount
                  RequeuedCount = metrics.RequeuedCount }
        with ex ->
            Error $"search_index_outbox_sweep_failed: {ex.Message}"

module SearchIndexJobProcessor =
    let private claimJobs (batchSize: int) (maxAttempts: int) (conn: NpgsqlConnection) =
        use cmd =
            new NpgsqlCommand(
                """
with candidate as (
  select job_id, tenant_id, version_id, attempts
  from search_index_jobs
  where status in ('pending', 'failed')
    and available_at <= now()
    and attempts < @max_attempts
  order by available_at, created_at
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
select job_id, tenant_id, version_id, attempts
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
  m.manifest_json::text
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
            let manifestJson = if reader.IsDBNull(9) then None else Some(reader.GetString(9))

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
                      ManifestJson = manifestJson }
            )
        else
            Ok None

    let private buildSearchText (source: SearchIndexSourceRecord) =
        let fields =
            [ source.RepoKey
              source.PackageType
              source.PackageNamespace |> Option.defaultValue ""
              source.PackageName
              source.PackageVersion
              source.ManifestJson |> Option.defaultValue "" ]

        fields
        |> List.map (fun value -> value.Trim())
        |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> String.concat " "

    let private upsertSearchDocument (source: SearchIndexSourceRecord) (conn: NpgsqlConnection) =
        let searchText = buildSearchText source

        use cmd =
            new NpgsqlCommand(
                """
insert into search_documents
  (tenant_id, repo_id, version_id, repo_key, package_type, package_namespace, package_name, package_version, manifest_json, published_at, search_text, indexed_at, updated_at)
values
  (@tenant_id, @repo_id, @version_id, @repo_key, @package_type, @package_namespace, @package_name, @package_version, @manifest_json, @published_at, @search_text, now(), now())
on conflict (tenant_id, version_id)
do update set
  repo_id = excluded.repo_id,
  repo_key = excluded.repo_key,
  package_type = excluded.package_type,
  package_namespace = excluded.package_namespace,
  package_name = excluded.package_name,
  package_version = excluded.package_version,
  manifest_json = excluded.manifest_json,
  published_at = excluded.published_at,
  search_text = excluded.search_text,
  indexed_at = now(),
  updated_at = now();
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

        let manifestParam = cmd.Parameters.Add("manifest_json", NpgsqlTypes.NpgsqlDbType.Jsonb)
        manifestParam.Value <- (match source.ManifestJson with | Some value -> box value | None -> box DBNull.Value)

        let publishedAtParam = cmd.Parameters.Add("published_at", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        publishedAtParam.Value <- (match source.PublishedAtUtc with | Some value -> box value | None -> box DBNull.Value)

        cmd.Parameters.AddWithValue("search_text", searchText) |> ignore
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

    let runSweep (connectionString: string) (batchSize: int) (maxAttempts: int) =
        try
            use conn = new NpgsqlConnection(connectionString)
            conn.Open()

            let claimed = claimJobs batchSize maxAttempts conn
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
        | "" -> "Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress"
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

module Program =
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

        printfn "Artifortress worker started. Press Ctrl+C to stop."
        printfn
            "worker_config batch_size=%d poll_seconds=%d run_once=%b search_job_max_attempts=%d"
            batchSize
            pollSeconds
            runOnce
            maxSearchJobAttempts

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
                printfn "worker_search_sweep_error_utc=%O error=\"%s\"" DateTimeOffset.UtcNow err

            match SearchIndexJobProcessor.runSweep connectionString batchSize maxSearchJobAttempts with
            | Ok outcome ->
                printfn
                    "worker_job_sweep_utc=%O claimed=%d completed=%d failed=%d"
                    DateTimeOffset.UtcNow
                    outcome.ClaimedCount
                    outcome.CompletedCount
                    outcome.FailedCount
            | Error err ->
                printfn "worker_job_sweep_error_utc=%O error=\"%s\"" DateTimeOffset.UtcNow err

        if runOnce then
            runSweepAndLog ()
        else
            while not stopToken.IsCancellationRequested do
                runSweepAndLog ()
                Thread.Sleep(TimeSpan.FromSeconds(float pollSeconds))

        printfn "Artifortress worker stopped."
        0
