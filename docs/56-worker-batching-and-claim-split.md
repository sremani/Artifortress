# Worker Batching and Claim Split

## Objective

This tranche closes the next worker optimization phase:
- `WO-2-01` rewrite the outbox handoff as a set-based SQL unit
- `WO-2-02` batch claimed events in `SearchIndexOutboxProducer.runSweep`
- `WO-2-03` preserve replay and duplicate-delivery guarantees for batched outbox handoff
- `WO-3-01` split `claimJobs` into pending/failed and stale-processing claim paths
- `WO-3-02` preserve reclaim behavior after the split
- `WO-3-03` compare post-split claim-path plan shape

Relevant code:
- `src/Artifortress.Worker/Program.fs`

## What Changed

### Batched outbox handoff

The worker no longer performs the outbox-to-job handoff one event at a time.

Previous shape:
- loop each claimed outbox event
- insert or update one `search_index_jobs` row
- update one `outbox_events` row
- commit one transaction per event

New shape:
- partition claimed events into:
  - routable version events
  - malformed/requeue events
- batch requeue malformed events with one `update ... where event_id = any(@event_ids)`
- batch routable events through one SQL unit that:
  - unnests event/tenant/version arrays
  - deduplicates `(tenant_id, version_id)` for job upsert
  - upserts `search_index_jobs`
  - marks all contributing `outbox_events` rows as delivered

Important semantic detail:
- duplicate publish events for the same version still count as multiple enqueued and delivered events
- they still collapse to one `search_index_jobs` row because the job identity remains `(tenant_id, version_id)`

This preserves the replay contract used by:
- duplicate publish replay tests
- malformed publish replay tests

### Split job claim path

The worker no longer claims pending/failed and stale-processing jobs through one mixed `OR` query.

New shape:
- `claimPendingOrFailedJobs`
- `claimStaleProcessingJobs`
- `claimJobs` now:
  - claims pending/failed work first
  - uses any remaining batch capacity to reclaim stale-processing jobs

This makes the behavior easier to reason about and easier for the planner to optimize.

## Validation

Validation performed for this tranche:
- `dotnet build Artifortress.sln -c Debug`
- `make test-integration`
- `bash scripts/reliability-drill.sh`
- `bash scripts/search-soak-drill.sh`

Results:
- all passed when run serially

Note:
- the worker-sensitive drills remain shared-state tests and should be run serially for reliable interpretation

## Post-Change Plan Shape

### Pending / failed claim path

Observed plan:
- current local dataset still chooses a `Seq Scan on search_index_jobs`
- followed by anti-join against `tenant_search_controls`
- followed by sort on `available_at, created_at`

Interpretation:
- this is acceptable on the current tiny local dataset
- the important change is query-shape separation, not forcing an index on every small-table plan
- the dedicated pending/failed index remains available for larger data distributions

### Stale-processing reclaim path

Observed plan:
- `Index Scan using idx_search_index_jobs_processing_reclaim`
- anti-join against `tenant_search_controls`
- incremental sort on `updated_at, created_at`

Interpretation:
- the stale-processing reclaim path now has a direct dedicated planner path
- this is the expected improvement for reclaim-specific work

## Outcome

This tranche reduces worker round trips and transaction churn in the outbox handoff and makes the claim logic structurally cleaner.

It does not yet reduce:
- search document write amplification
- source-read payload size
- command parse/setup overhead

Those remain the focus of:
- `WO-4`
- `WO-5`
- `WO-6`
