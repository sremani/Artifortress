# Worker Read/Write Reduction

This note captures the `WO-4` and `WO-5` tranche that reduced avoidable worker
churn on the search indexing path.

## Scope

Files changed:
- `src/Kublai.Worker/Program.fs`
- `docs/54-worker-indexing-optimization-board.md`

Validated with:
- `dotnet build Kublai.sln -c Debug`
- `make test-integration`
- `bash scripts/reliability-drill.sh`
- `bash scripts/search-soak-drill.sh`

## Changes

### Reduced-write `search_documents` upsert

`upsertSearchDocument` now avoids rewriting `search_documents` when the indexed
payload is unchanged.

Implementation details:
- `ON CONFLICT DO UPDATE` is guarded with `IS DISTINCT FROM` comparisons across
  the indexed payload columns.
- `indexed_at` and `updated_at` are only advanced when the row actually changes.
- The persisted projection now carries the search payload only; full
  `manifest_json` is no longer copied into `search_documents`.

Result:
- replay and reindex paths avoid unnecessary row churn
- `search_tsv` maintenance is skipped on no-op replays

### Reduced source-read payload

`tryReadSearchIndexSource` now returns only the payload required by
`upsertSearchDocument`.

Implementation details:
- the worker no longer reads `manifest_json` into F# just to rebuild the search
  text
- SQL now constructs the normalized `search_text` payload directly with
  `concat_ws(...)`
- the worker record returned from the source-read query now carries the exact
  text written into `search_documents`

Result:
- smaller worker-side materialization cost
- less F# allocation and string assembly on the indexing path

## Validation Notes

The first draft of this tranche introduced a malformed `ON CONFLICT` statement.
That bug was corrected before the final validation run. The validated version is
the one described in this document.

Validation results:
- full integration suite passed: `112/112`
- reliability drill passed
- search soak drill passed

## Remaining Board Work

Still open after this tranche:
- `WO-5-03` re-profile worker indexing path after read/write reductions
- `WO-6-*` command reuse and final measurement
