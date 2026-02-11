# Consolidated Change Summary

Last updated: 2026-02-11

## Scope of This Change Set

This change set combines:
- Domain/API validation hardening for repository keys.
- Unit + integration regression coverage for the hardening path.
- FsCheck property-based testing expansion across domain and API helpers.
- Three extraction waves in the worker to expose pure internals for deeper property testing.
- Phase 3 publish-workflow completion (entries, manifests, atomic publish, outbox, and audit coverage).
- Documentation updates reflecting implementation and verification state.

## Code Changes

### 1. Domain/API hardening

- `src/Artifortress.Domain/Library.fs`
  - `RepoScope.tryCreate` now rejects repository keys containing `:`.

- `src/Artifortress.Api/Program.fs`
  - `validateRepoRequest` now rejects `repoKey` values containing `:`.

### 2. Worker internals extraction (three waves)

- `src/Artifortress.Worker/WorkerInternals.fs` (new)
  - Wave 1:
    - `WorkerOutboxParsing`
    - `WorkerRetryPolicy`
    - `WorkerEnvParsing`
  - Wave 2:
    - `WorkerDbParameters`
    - `WorkerOutboxFlow`
    - `WorkerJobFlow`
  - Wave 3:
    - `WorkerDataShapes`
    - `WorkerSweepMetrics`

- `src/Artifortress.Worker/Program.fs`
  - Rewired outbox/job sweep logic to consume extracted helpers.
  - Replaced inline sweep counters and row constructors with pure reducer/shape modules.
  - Normalized DB query parameter handling through `WorkerDbParameters`.

- `src/Artifortress.Worker/Artifortress.Worker.fsproj`
  - Added compile include for `WorkerInternals.fs`.

### 3. Phase 3 publish workflow completion

- `db/migrations/0007_phase3_manifest_persistence.sql` (new)
  - Added `manifests` persistence table and supporting indexes.

- `src/Artifortress.Api/Program.fs`
  - Added request models:
    - `UpsertArtifactEntriesRequest`
    - `UpsertManifestRequest`
  - Added validation scaffolding:
    - artifact entry payload validation (digest/checksum/path/size rules),
    - package-type manifest validation (`nuget`, `npm`, `maven`, generic object baseline).
  - Added new API endpoints:
    - `POST /v1/repos/{repoKey}/packages/versions/{versionId}/entries`
    - `PUT /v1/repos/{repoKey}/packages/versions/{versionId}/manifest`
    - `GET /v1/repos/{repoKey}/packages/versions/{versionId}/manifest`
    - `POST /v1/repos/{repoKey}/packages/versions/{versionId}/publish`
  - Added single-transaction publish flow with:
    - draft-state guards,
    - artifact-entry + manifest precondition enforcement,
    - in-transaction outbox event emission (`version.published`),
    - in-transaction publish audit write,
    - idempotent republish response path without duplicate outbox emission.

## Test Changes

- `tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj`
  - Added `PropertyTests.fs` compile include.
  - Added `FsCheck.Xunit` package reference.

- `tests/Artifortress.Domain.Tests/Tests.fs`
  - Added unit test for colon-rejecting repo keys.

- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added integration test for create-repo colon rejection behavior.
  - Added Phase 3 integration coverage:
    - artifact entry authz and blob-existence checks,
    - duplicate artifact path rejection,
    - manifest validation/query behavior,
    - atomic publish success + idempotent republish,
    - publish authz + audit matrix,
    - deterministic post-publish mutation conflicts,
    - publish failure rollback safety (no partial state/outbox).

- `tests/Artifortress.Domain.Tests/PropertyTests.fs` (new)
  - Expanded to `75` FsCheck properties covering:
    - domain role/scope/auth invariants,
    - API validation/parsing/range/digest invariants,
    - object storage config normalization,
    - extracted worker helper invariants across all three extraction waves.

## Documentation Changes

- `README.md`
  - Updated status and API surface for completed Phase 3 endpoints.

- `docs/10-current-state.md`
  - Updated verification counts and implementation inventory including Phase 3 completion.

- `docs/12-phase3-implementation-tickets.md`
  - Marked P3-03 through P3-10 complete and documented verification evidence.

- `docs/19-phase3-runbook.md` (new)
  - Added executable Phase 3 draft/manifest/publish runbook.

- `scripts/phase3-demo.sh` (new)
  - Added script that exercises Phase 3 workflow end-to-end.

- `Makefile`
  - Added `phase3-demo` target.

- `docs/14-worker-pbt-extraction-tickets.md` (new)
  - Worker extraction ticket board and implementation notes.

- `docs/15-change-summary.md` (this file)
  - Consolidated summary of all changes in this set.

## Latest Verification

- `make format` passed.
- `make test` (non-integration filter) passed: `84` tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --configuration Debug --no-build -v minimal --filter "Category=Integration"` passed: `53` integration tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -nologo --configuration Debug --no-build -v minimal` passed: `137` total tests.

## Phase 5 Completion Addendum (2026-02-11)

### Code changes

- `db/migrations/0008_phase5_tombstones_gc_reconcile.sql` (new)
  - Added `tombstones`, `gc_runs`, and `gc_marks` lifecycle tables and indexes.
  - Updated `upload_sessions_committed_blob_digest_fkey` behavior to `ON DELETE SET NULL`.
- `src/Artifortress.Api/ObjectStorage.fs`
  - Added `DeleteObject` to `IObjectStorageClient` and S3 implementation.
- `src/Artifortress.Api/Program.fs`
  - Added tombstone endpoint:
    - `POST /v1/repos/{repoKey}/packages/versions/{versionId}/tombstone`
  - Added admin GC/reconcile endpoints:
    - `POST /v1/admin/gc/runs`
    - `GET /v1/admin/reconcile/blobs`
  - Added tombstone/GC/reconcile validation, DB handlers, and audit integration.
  - Fixed GC dry-run safety to avoid version/blob mutations.
  - Added upload-session committed-blob reference cleanup before blob hard-delete.
- `scripts/phase5-demo.sh` (new)
  - Added executable Phase 5 end-to-end workflow script.
- `Makefile`
  - Added `phase5-demo` target.

### Test changes

- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added Phase 5 integration tests:
    - tombstone authz/transition/idempotency (`P5-01`)
    - GC dry-run safety and candidate reporting (`P5-02`)
    - GC execute hard-delete flow (`P5-03`)
    - reconcile drift summary (`P5-04`)
    - admin endpoint authz matrix (`P5-05`)

### Documentation changes

- `docs/20-phase5-implementation-tickets.md` (new)
  - Added full Phase 5 ticket board, notes, and acceptance criteria.
- `docs/21-phase5-runbook.md` (new)
  - Added executable runbook for Phase 5 lifecycle demo.
- Updated:
  - `README.md`
  - `docs/05-implementation-plan.md`
  - `docs/07-delivery-walkthrough-plan.md`
  - `docs/10-current-state.md`

### Updated verification

- `make build` passed.
- `make test` passed: `84` tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --configuration Debug --no-build -v minimal --filter "Category=Integration"` passed: `53` integration tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -nologo --configuration Debug --no-build -v minimal` passed: `137` total tests.
- `make format` passed.
