# Worker Claim-Path Indexing

## Objective

This tranche closes the first worker optimization phase:
- `WO-1-01` add an outbox claim-path index
- `WO-1-02` add a stale-processing reclaim index
- `WO-1-03` tighten the pending/failed claim index
- `WO-1-04` capture `EXPLAIN` evidence for the worker claim queries

Relevant code:
- `src/Kublai.Worker/Program.fs`

Relevant migration:
- `db/migrations/0015_worker_claim_path_indexes.sql`

## Index Changes

### Outbox claim path

Added:
- `idx_outbox_version_published_claim`

Shape:
- partial index on `outbox_events`
- predicate:
  - `delivered_at is null`
  - `event_type = 'version.published'`
- key order:
  - `occurred_at`
  - `available_at`
  - `tenant_id`

Purpose:
- match the worker query in `SearchIndexOutboxProducer.claimOutboxEvents`
- give the planner a path better aligned with:
  - pending published events
  - oldest-first claim order
  - tenant pause anti-join

### Search job pending/failed claim path

Replaced:
- `idx_search_index_jobs_pending`

With:
- `idx_search_index_jobs_pending_claim`

Shape:
- partial index on `search_index_jobs`
- predicate:
  - `status in ('pending', 'failed')`
- key order:
  - `status`
  - `available_at`
  - `created_at`
  - `tenant_id`

Purpose:
- align with the worker ordering in `SearchIndexJobProcessor.claimJobs`
- reduce planner need for extra sort work on the pending/failed path

### Search job stale-processing reclaim path

Added:
- `idx_search_index_jobs_processing_reclaim`

Shape:
- partial index on `search_index_jobs`
- predicate:
  - `status = 'processing'`
- key order:
  - `status`
  - `updated_at`
  - `tenant_id`

Purpose:
- provide a direct reclaim path for stale processing jobs
- avoid treating reclaim as an incidental side effect of the pending/failed index set

## Explain Evidence

The claim queries were checked with `EXPLAIN` after applying the migration.

### `claimOutboxEvents`

Observed plan shape:
- `Bitmap Index Scan on idx_outbox_version_published_claim`
- `Bitmap Heap Scan on outbox_events`
- `Hash Anti Join` against `tenant_search_controls`
- explicit `Sort` on `outbox_events.occurred_at`

Interpretation:
- the new partial outbox index is being used for the pending published-event filter
- the claim path is materially better aligned than the previous generic pending index path
- the planner still chooses an explicit sort for `order by occurred_at`

This is an acceptable tranche-1 result because the index now narrows the pending
published-event set directly. The remaining sort pressure is a good input to
`WO-2` and `WO-3`, where query shape changes are on the table.

### `claimJobs`

Observed plan shape:
- `BitmapOr`
  - `Bitmap Index Scan on idx_search_index_jobs_pending_claim`
  - `Bitmap Index Scan on idx_search_index_jobs_processing_reclaim`
- `Bitmap Heap Scan on search_index_jobs`
- `Nested Loop Anti Join` against `tenant_search_controls`
- `Index Scan using search_index_jobs_pkey` for the update target

Interpretation:
- both new job-path indexes are in active use
- pending/failed claims and stale-processing reclaim now have dedicated planner paths
- this is the intended outcome for tranche 1

The exact plan output should be refreshed whenever this tranche is replayed in a
different data distribution or Postgres version.

## Validation

Recommended validation battery for this tranche:
- `make db-migrate`
- `make test`
- `make test-integration`
- `bash scripts/reliability-drill.sh`
- `bash scripts/search-soak-drill.sh`

## Notes

This tranche does not change worker behavior. It changes only:
- index availability
- planner options
- the evidence base for later batching/query-shape work

The next tranche should focus on:
- `WO-2` outbox handoff batching
- `WO-3` job-claim simplification
