# Search Backfill Soak

This tranche closes `ER-4-04` large-index rebuild and backfill soak coverage.

## Scope

The soak path exercises search rebuild behavior under a sustained backlog rather than
only single-sweep correctness:
- a large repo-scoped rebuild enqueues a full published-version backlog
- search jobs are processed in multiple small worker sweeps
- a new published version arrives while the backlog is still draining
- the outbox producer and rebuild control plane are both exercised mid-flight

The intent is to prove that long-running backfills do not silently drift, strand tail
items, or create duplicate search artifacts.

## Invariants

The soak coverage asserts the following:
- all published versions eventually project into exactly one `search_documents` row
- the backlog shrinks over time rather than starving tail items
- no failed jobs or stale `processing` jobs appear during the run
- a package published during the backfill is still indexed and queryable before the run completes
- final search status returns zero pending, processing, failed, stale-processing, and stale-document counts

## Operator Drill

`scripts/search-soak-drill.sh` runs the sustained backfill test and writes a report to:
- `docs/reports/search-soak-drill-latest.md`

Recommended execution:
- `make test`
- `make test-integration`
- `make search-soak-drill`

## Acceptance

`ER-4-04` is considered satisfied in this repository because:
- the search rebuild path is exercised under a multi-round backlog
- mid-flight publishes are incorporated without duplicate search rows
- completion is verified through the supported admin status endpoint and persisted search projections
