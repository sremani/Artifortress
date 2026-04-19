# Worker Indexing Optimization Board

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved
- `deferred`: intentionally postponed for a future milestone

## Objective

This board tracks the focused optimization program for the Artifortress worker
indexing and persistence path identified during CLR profiling.

The target path is:
- outbox claim
- outbox-to-job handoff
- search job claim
- search source read
- search document upsert
- job completion update

Primary profiling evidence for this board:
- `/tmp/artifortress-worker-trace-concurrent.nettrace`
- `/tmp/artifortress-worker-trace-concurrent.speedscope.json`
- `/tmp/artifortress-worker-counters-concurrent.json`

Relevant implementation files:
- `src/Artifortress.Worker/Program.fs`
- `db/migrations/0001_init.sql`
- `db/migrations/0004_phase3_publish_guardrails.sql`
- `db/migrations/0006_phase4_policy_search_quarantine_scaffold.sql`
- `db/migrations/0009_post_ga_search_read_model.sql`
- `db/migrations/0011_tenant_isolation_and_admission_controls.sql`
- `db/migrations/0014_search_assurance_controls.sql`

## Phase Map

| Phase | Goal | Primary Outcome |
|---|---|---|
| WO-1 | Claim-path indexing | worker claim queries match storage layout |
| WO-2 | Outbox handoff batching | fewer per-event round trips and commits |
| WO-3 | Job claim simplification | planner-friendly claim semantics |
| WO-4 | Search document write reduction | lower write amplification on `search_documents` |
| WO-5 | Search source read reduction | smaller and cheaper per-job source reads |
| WO-6 | Command reuse | lower recurring Npgsql parse/setup overhead |

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| WO-1-01 | Add outbox pending index for `version.published` claim path | none | done |
| WO-1-02 | Add stale-processing reclaim index for `search_index_jobs` | WO-1-01 | done |
| WO-1-03 | Tighten pending/failed claim index for `search_index_jobs` ordering | WO-1-01 | done |
| WO-1-04 | Capture `EXPLAIN` evidence for `claimOutboxEvents` and `claimJobs` | WO-1-02, WO-1-03 | done |
| WO-2-01 | Rewrite outbox handoff as a set-based SQL unit | WO-1-04 | done |
| WO-2-02 | Batch claimed events in `SearchIndexOutboxProducer.runSweep` | WO-2-01 | done |
| WO-2-03 | Add replay and duplicate-delivery regression coverage for batched outbox handoff | WO-2-02 | done |
| WO-3-01 | Split `claimJobs` into pending/failed and stale-processing claim paths | WO-1-04 | done |
| WO-3-02 | Add reclaim-specific integration coverage for split claim behavior | WO-3-01 | done |
| WO-3-03 | Compare pre/post split claim-path plans and counters | WO-3-02 | done |
| WO-4-01 | Audit `upsertSearchDocument` write set for avoidable column churn | WO-3-03 | todo |
| WO-4-02 | Skip no-op `search_documents` updates when indexed content is unchanged | WO-4-01 | todo |
| WO-4-03 | Reassess `manifest_json` storage on `search_documents` versus search requirements | WO-4-01 | todo |
| WO-4-04 | Add replay/idempotency coverage for reduced-write search upserts | WO-4-02 | todo |
| WO-5-01 | Trim `tryReadSearchIndexSource` to the minimal indexing payload | WO-3-03 | todo |
| WO-5-02 | Validate source-read query shape and indexes against the reduced payload | WO-5-01 | todo |
| WO-5-03 | Re-profile worker indexing path after read/write reductions | WO-4-04, WO-5-02 | todo |
| WO-6-01 | Prepare or reuse stable Npgsql commands in hot worker paths | WO-5-03 | todo |
| WO-6-02 | Measure parse/process overhead reduction after command reuse | WO-6-01 | todo |
| WO-6-03 | Publish final before/after optimization report | WO-6-02 | todo |

## Current Sequencing Guidance

Recommended execution order:
1. `WO-1` claim-path indexing
2. `WO-2` outbox handoff batching
3. `WO-3` job claim simplification
4. `WO-4` search document write reduction
5. `WO-5` search source read reduction
6. `WO-6` command reuse and final measurement

Rationale:
- The current worker profile is dominated by database access and round-trip cost.
- Index and query-shape fixes should land before code-level micro-optimizations.
- Prepared command reuse is worth doing, but only after bigger structural wins are in.

## Ticket Details

## WO-1 Claim-Path Indexing

### WO-1-01: Add outbox pending index for `version.published` claim path

Scope:
- Add a partial index for `outbox_events` matching the claim predicate in `claimOutboxEvents`.
- Shape the index for `delivered_at is null`, `event_type = 'version.published'`, and the worker ordering requirements.

Acceptance criteria:
- `EXPLAIN` shows index usage for the claim query without broad pending-row scans.
- Publish backlog drain remains functionally unchanged.

### WO-1-02: Add stale-processing reclaim index for `search_index_jobs`

Scope:
- Add an index shaped for reclaiming `processing` jobs by `updated_at`.

Acceptance criteria:
- Reclaim query avoids broad scans of unrelated `search_index_jobs` rows.
- Reliability drill continues to pass.

### WO-1-03: Tighten pending/failed claim index for `search_index_jobs` ordering

Scope:
- Add or adjust an index to match `pending` / `failed` filtering plus the worker ordering by `available_at, created_at`.

Acceptance criteria:
- Claim query uses the new index for the pending/failed path.

### WO-1-04: Capture `EXPLAIN` evidence for `claimOutboxEvents` and `claimJobs`

Scope:
- Record pre/post plan shape for the two main worker claim queries.

Acceptance criteria:
- The board has concrete evidence for the indexing tranche, not just inferred improvement.

## WO-2 Outbox Handoff Batching

### WO-2-01: Rewrite outbox handoff as a set-based SQL unit

Scope:
- Replace per-event insert/update/commit behavior in `enqueueSearchJobAndMarkDelivered` with a set-based SQL path.

Acceptance criteria:
- Idempotency is preserved.
- Outbox rows and search jobs remain consistent under failure/retry.

### WO-2-02: Batch claimed events in `SearchIndexOutboxProducer.runSweep`

Scope:
- Stop looping each claimed event through its own persistence transaction.
- Apply the handoff in batches.

Acceptance criteria:
- Worker throughput improves under publish-heavy load.
- Duplicate job creation does not regress.

### WO-2-03: Add replay and duplicate-delivery regression coverage for batched outbox handoff

Scope:
- Extend tests and drills for batched event delivery semantics.

Acceptance criteria:
- Existing replay guarantees still hold after batching.

## WO-3 Job Claim Simplification

### WO-3-01: Split `claimJobs` into pending/failed and stale-processing claim paths

Scope:
- Replace the current `OR`-based claim query with separate planner-friendly claim paths.

Acceptance criteria:
- Functional behavior remains the same.
- Query plans become simpler and more index-friendly.

### WO-3-02: Add reclaim-specific integration coverage for split claim behavior

Scope:
- Validate stale-processing reclaim and fresh-processing non-reclaim semantics after the split.

Acceptance criteria:
- Reclaim-related tests remain deterministic and green.

### WO-3-03: Compare pre/post split claim-path plans and counters

Scope:
- Re-measure worker claim behavior after the split.

Acceptance criteria:
- Counter and plan evidence demonstrates whether the split materially helped.

## WO-4 Search Document Write Reduction

### WO-4-01: Audit `upsertSearchDocument` write set for avoidable column churn

Scope:
- Identify columns rewritten unnecessarily on every successful index job.

Acceptance criteria:
- A concrete reduced-write plan exists before code changes land.

### WO-4-02: Skip no-op `search_documents` updates when indexed content is unchanged

Scope:
- Guard `ON CONFLICT DO UPDATE` so unchanged index payloads avoid full row rewrites.

Acceptance criteria:
- Replay and reindex scenarios generate less write churn.
- Search semantics remain unchanged.

### WO-4-03: Reassess `manifest_json` storage on `search_documents` versus search requirements

Scope:
- Decide whether full manifest persistence in the search projection is required.

Acceptance criteria:
- The choice is explicit and reflected in implementation/docs.

### WO-4-04: Add replay/idempotency coverage for reduced-write search upserts

Scope:
- Prove reduced-write logic does not break replay safety.

Acceptance criteria:
- Repeated indexing leaves one correct search row per version.

## WO-5 Search Source Read Reduction

### WO-5-01: Trim `tryReadSearchIndexSource` to the minimal indexing payload

Scope:
- Remove unnecessary data fetches from the source-read query.

Acceptance criteria:
- Query returns only fields required by `upsertSearchDocument`.

### WO-5-02: Validate source-read query shape and indexes against the reduced payload

Scope:
- Inspect and, if needed, tune indexes for the reduced read shape.

Acceptance criteria:
- Source-read cost does not increase after payload changes.

### WO-5-03: Re-profile worker indexing path after read/write reductions

Scope:
- Repeat CLR counters and traces after `WO-4` and `WO-5`.

Acceptance criteria:
- There is clear before/after evidence for worker indexing improvements.

## WO-6 Command Reuse

### WO-6-01: Prepare or reuse stable Npgsql commands in hot worker paths

Scope:
- Reuse/pre-prepare the most stable commands in the worker sweep path.

Acceptance criteria:
- Command parse/setup overhead is reduced without harming correctness.

### WO-6-02: Measure parse/process overhead reduction after command reuse

Scope:
- Re-profile and compare Npgsql command-processing visibility in traces.

Acceptance criteria:
- Trace evidence shows command overhead moved down materially or is no longer worth further work.

### WO-6-03: Publish final before/after optimization report

Scope:
- Capture the final result of the worker optimization program in one summary report.

Acceptance criteria:
- The repository contains a single reference doc for optimization outcome, impact, and residual risks.
