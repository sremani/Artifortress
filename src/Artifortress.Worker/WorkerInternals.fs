namespace Artifortress.Worker

open System
open System.Text.Json

module WorkerOutboxParsing =
    let private tryParseGuid (value: string) =
        match Guid.TryParse(value) with
        | true, parsed -> Some parsed
        | _ -> None

    let tryResolveVersionId (aggregateId: string) (payloadJson: string) =
        match tryParseGuid aggregateId with
        | Some versionId -> Some versionId
        | None ->
            try
                use doc = JsonDocument.Parse(payloadJson)
                let root = doc.RootElement
                let mutable versionIdProperty = Unchecked.defaultof<JsonElement>

                if root.ValueKind = JsonValueKind.Object && root.TryGetProperty("versionId", &versionIdProperty) then
                    if versionIdProperty.ValueKind = JsonValueKind.String then
                        versionIdProperty.GetString() |> tryParseGuid
                    else
                        None
                else
                    None
            with _ ->
                None

module WorkerRetryPolicy =
    [<Literal>]
    let BaseDelaySeconds = 30

    [<Literal>]
    let MaxBackoffExponent = 5

    let private clampMinOne (value: int) = max 1 value

    let computeBackoffExponent (attempts: int) =
        let safeAttempts = clampMinOne attempts
        min (safeAttempts - 1) MaxBackoffExponent

    let computeBackoffSeconds (attempts: int) =
        BaseDelaySeconds * (pown 2 (computeBackoffExponent attempts))

    let incrementAttempts (currentAttempts: int) =
        max 0 currentAttempts |> fun value -> value + 1

    let computeNextAvailableUtc (referenceUtc: DateTimeOffset) (attempts: int) =
        referenceUtc.AddSeconds(float (computeBackoffSeconds attempts))

    let computeFailureSchedule (referenceUtc: DateTimeOffset) (currentAttempts: int) =
        let nextAttempts = incrementAttempts currentAttempts
        let nextAvailableUtc = computeNextAvailableUtc referenceUtc nextAttempts
        nextAttempts, nextAvailableUtc

module WorkerEnvParsing =
    let parsePositiveIntOrDefault (defaultValue: int) (rawValue: string) =
        match Int32.TryParse(rawValue) with
        | true, value when value >= 1 -> value
        | _ -> defaultValue

module WorkerDbParameters =
    let normalizeBatchSize (batchSize: int) = max 1 batchSize
    let normalizeMaxAttempts (maxAttempts: int) = max 1 maxAttempts

module WorkerOutboxFlow =
    type OutboxRoutingDecision =
        | EnqueueVersion of Guid
        | Requeue

    let decideRouting (aggregateId: string) (payloadJson: string) =
        match WorkerOutboxParsing.tryResolveVersionId aggregateId payloadJson with
        | Some versionId -> EnqueueVersion versionId
        | None -> Requeue

module WorkerJobFlow =
    type JobProcessingDecision =
        | Complete
        | Fail of Attempts: int * AvailableAtUtc: DateTimeOffset * ErrorMessage: string

    let decideProcessing (referenceUtc: DateTimeOffset) (currentAttempts: int) (isPublished: bool) =
        if isPublished then
            Complete
        else
            let attempts, availableAtUtc = WorkerRetryPolicy.computeFailureSchedule referenceUtc currentAttempts
            Fail(Attempts = attempts, AvailableAtUtc = availableAtUtc, ErrorMessage = "version_not_published")

module WorkerDataShapes =
    type ClaimedOutboxEvent = {
        EventId: Guid
        TenantId: Guid
        AggregateId: string
        PayloadJson: string
    }

    type ClaimedSearchJob = {
        JobId: Guid
        TenantId: Guid
        VersionId: Guid
        Attempts: int
    }

    let createClaimedOutboxEvent (eventId: Guid) (tenantId: Guid) (aggregateId: string) (payloadJson: string) =
        { EventId = eventId
          TenantId = tenantId
          AggregateId = aggregateId
          PayloadJson = payloadJson }

    let createClaimedSearchJob (jobId: Guid) (tenantId: Guid) (versionId: Guid) (attempts: int) =
        { JobId = jobId
          TenantId = tenantId
          VersionId = versionId
          Attempts = attempts }

module WorkerSweepMetrics =
    type OutboxMetrics = {
        EnqueuedCount: int
        DeliveredCount: int
        RequeuedCount: int
    }

    type JobMetrics = {
        CompletedCount: int
        FailedCount: int
    }

    let zeroOutbox =
        { EnqueuedCount = 0
          DeliveredCount = 0
          RequeuedCount = 0 }

    let zeroJobs =
        { CompletedCount = 0
          FailedCount = 0 }

    let recordRequeue metrics =
        { metrics with
            RequeuedCount = metrics.RequeuedCount + 1 }

    let recordEnqueue (wasDelivered: bool) metrics =
        { metrics with
            EnqueuedCount = metrics.EnqueuedCount + 1
            DeliveredCount = metrics.DeliveredCount + if wasDelivered then 1 else 0 }

    let recordCompleted metrics =
        { metrics with
            CompletedCount = metrics.CompletedCount + 1 }

    let recordFailed metrics =
        { metrics with
            FailedCount = metrics.FailedCount + 1 }
