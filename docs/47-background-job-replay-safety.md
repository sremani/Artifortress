# Background Job Replay Safety

Last updated: 2026-04-06

## Purpose

Record the replay and duplicate-delivery guarantees for Kublai background processing.

## Reviewed Paths

### Publish to Search Outbox

- source table: `outbox_events`
- worker path: `SearchIndexOutboxProducer.runSweep`
- replay behavior:
  - duplicate `version.published` events are independently claimable
  - each event is marked delivered at most once
  - search work is collapsed to a single `search_index_jobs` row per `(tenant_id, version_id)` by upsert
- externally visible effect:
  - duplicate publish delivery does not create duplicate search documents

### Search Job Processing

- source table: `search_index_jobs`
- worker path: `SearchIndexJobProcessor.runSweep`
- replay behavior:
  - only one row exists per `(tenant_id, version_id)`
  - stale `processing` jobs are reclaimed after lease expiry
  - search document writes are idempotent through `search_documents` upsert on `(tenant_id, version_id)`
  - failed retries use bounded backoff and max-attempt ceilings
- externally visible effect:
  - repeated search-job processing updates one search document row rather than multiplying records

### Malformed Publish Events

- malformed `version.published` events are requeued with delayed availability
- malformed events do not create search jobs
- malformed events do not create search documents

### Quarantine, Tombstone, GC, and Reconcile

- quarantine release/reject and tombstone API flows already expose idempotent or conflict-safe behavior at the HTTP contract layer
- GC and reconcile paths are batch-oriented and already covered by phase 5 integration coverage for repeated runs and preservation rules
- no additional background queue was introduced for these paths in the current repository state

### Audit-Adjacent Noise Events

- non-search outbox events such as `audit.replayed` are ignored by the search outbox sweep
- they remain outside the search job pipeline and do not create search artifacts

## Drill Coverage

`scripts/reliability-drill.sh` now includes:

- `P4-04 outbox sweep enqueues search index job and marks event delivered`
- `ER-3-01 duplicate publish replay preserves single search document and job record`
- `ER-3-01 repeated malformed publish replay does not create search jobs or documents`
- `ER-3-03 stale processing search job is reclaimed after lease expiry`
- `ER-3-03 fresh processing search job is not reclaimed before lease expiry`

## Acceptance Statement

The current background processing model is accepted as replay-safe for enterprise scope when all are true:

1. duplicate publish delivery does not create duplicate search rows
2. stale `processing` search jobs are reclaimable
3. malformed events do not fan out into new work
4. repeated runs preserve externally visible state
