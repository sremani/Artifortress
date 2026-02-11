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
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --configuration Debug --no-build -v minimal --filter "Category=Integration"` passed: `56` integration tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -nologo --configuration Debug --no-build -v minimal` passed: `140` total tests.

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
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --configuration Debug --no-build -v minimal --filter "Category=Integration"` passed: `56` integration tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -nologo --configuration Debug --no-build -v minimal` passed: `140` total tests.
- `make format` passed.

## Track B F# Mutation Spike Addendum (2026-02-11)

### Code and tooling changes

- `dotnet-tools.json` (new)
  - Added local tool manifest with `dotnet-stryker` 4.12.0.
- `tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj` (new)
  - Added mutation-only test project including unit and property tests (integration tests excluded by project composition).
- `scripts/mutation-fsharp-spike.sh` (new)
  - Added reproducible F# mutation feasibility spike command and report writer.
- `Makefile`
  - Added `mutation-spike` target.

### Documentation changes

- `docs/32-fsharp-mutation-track-tickets.md` (new)
  - Added Track B ticket board with MUT-01 through MUT-10 and current status.
- `docs/33-fsharp-mutation-trackb-plan.md` (new)
  - Added upstream/fork engineering strategy and milestone plan for F# mutation support.
- `docs/reports/mutation-spike-fsharp-latest.md` (new)
  - Added latest spike execution report artifact.
- Updated:
  - `README.md`
  - `docs/05-implementation-plan.md`
  - `docs/10-current-state.md`

### MUT-01 finding

- Running Stryker against the F# domain project currently fails with:
  - `System.NotSupportedException: Language not supported: Fsharp`
- Result: Track B requires engine-level upstream/fork work; wrapper-only integration is insufficient.

## Track B MUT-05 through MUT-10 Progress Addendum (2026-02-11)

### Code and tooling changes

- `tools/Artifortress.MutationTrack/Program.fs`
  - Expanded wrapper behavior:
    - added optional `--stryker-cli` path override to execute patched fork binaries.
    - added `no_mutants_generated` failure class.
    - added machine-readable JSON output (`--result-json`) alongside markdown report output.
- `patches/stryker-net/0001-mut06-fsharp-analyzer-pipeline.patch` (new)
  - Added reproducible MUT-06 patch artifact for F# analyzer-path compatibility experiment.
- `patches/stryker-net/0002-mut07a-fsharp-mutator-registration.patch` (new)
  - Added language-aware F# mutator registration entrypoint (operator-family profile).
- `patches/stryker-net/0003-mut07b-fsharp-operator-candidate-planner.patch` (new)
  - Added lexical F# operator candidate planner scaffold + associated fork unit tests.
- `patches/stryker-net/0004-mut08a-fsharp-source-span-mapper.patch` (new)
  - Added F# source span-mapping scaffold + associated fork unit tests.
- `scripts/mutation-trackb-bootstrap.sh` (new)
  - Added deterministic Stryker fork bootstrap + ordered multi-patch apply workflow + workspace cleaning.
- `scripts/mutation-trackb-build.sh` (new)
  - Added patched Stryker CLI build workflow.
- `scripts/mutation-trackb-spike.sh` (new)
  - Added one-command MUT-06 fork run through wrapper/reporting path.
- `Makefile`
  - Added targets:
    - `mutation-trackb-bootstrap`
    - `mutation-trackb-build`
    - `mutation-trackb-spike`
- `.github/workflows/mutation-track.yml` (new)
  - Added nightly/manual non-blocking mutation lane with artifact upload.

### Documentation changes

- `docs/32-fsharp-mutation-track-tickets.md`
  - Updated ticket statuses:
    - MUT-05: done
    - MUT-06: done
    - MUT-07: in_progress
    - MUT-08: in_progress
    - MUT-09: in_progress
    - MUT-10: in_progress
  - Added MUT-07/MUT-08 extracted subtasks and next execution order.
- `docs/33-fsharp-mutation-trackb-plan.md`
  - Updated milestone status:
    - B2 complete
    - B3 complete
    - B4 blocked
    - B5 in_progress
- Updated:
  - `README.md`
  - `docs/05-implementation-plan.md`
  - `docs/10-current-state.md`

### MUT-06 execution outcome

- Patched fork run reaches:
  - F# project identification,
  - build and initial test phases.
- Hard blocker removed:
  - no `Language not supported: Fsharp` exception in patched path.
- Remaining blocker:
  - `0 mutants created` (`no_mutants_generated`) pending MUT-07/MUT-08 engine work.

### MUT-07a / MUT-07b scaffold outcome

- Patched Track B run now logs:
  - experimental F# mutator profile registration (`3` mutators),
  - F# operator candidate discovery metrics in the mutation path.
- Remaining blocker:
  - no concrete F# source rewrite/mutant emission yet (MUT-07b and MUT-08 work remains).

### MUT-08a scaffold outcome

- Patched Track B run now logs:
  - mapped F# source span metrics for discovered operator candidates.
- Remaining blocker:
  - MUT-08b rewrite safety guards and invalid-rewrite handling are still pending.

## Track B MUT-07b/MUT-08 Progress Addendum (2026-02-11, later)

### Code and tooling changes

- `patches/stryker-net/0006-mut07b-fsharp-mutant-emission-quarantine.patch` (new)
  - Added F# operator mutant emission planning path (`FsharpOperatorMutantEmitter`).
  - Added compile-error quarantine status for emitted F# mutants pending runtime activation support.
- `patches/stryker-net/0007-mut07b-fsharp-path-selection.patch` (new)
  - Added mutate-glob matching against run-root relative paths for F# source selection.
- `tools/Artifortress.MutationTrack/Program.fs`
  - Updated default mutate scope to `src/Artifortress.Domain/Library.fs`.
  - Added `quarantined_compile_errors` failure class and next-action guidance.

### Latest Track B execution outcome

- `make mutation-trackb-spike` now reports:
  - `71` operator candidates,
  - `71` mapped spans,
  - `71` planned mutants,
  - `71 mutants created`.
- Current mutant lifecycle state:
  - all emitted mutants are quarantined as `CompileError` with reason:
    - `F# mutant is quarantined pending runtime activation support.`
- Wrapper classification now returns:
  - `quarantined_compile_errors` (instead of `no_mutants_generated`).

## Phase 6 Completion Addendum (2026-02-11)

### Code changes

- `src/Artifortress.Api/ObjectStorage.fs`
  - Added object storage readiness probe contract:
    - `CheckAvailability`.
- `src/Artifortress.Api/Program.fs`
  - Hardened readiness endpoint:
    - `/health/ready` now checks Postgres + object storage and returns `503` when not ready.
  - Added operations summary endpoint:
    - `GET /v1/admin/ops/summary`.
  - Added operations-summary audit action:
    - `ops.summary.read`.
  - Added GC failure finalization path to avoid indefinitely incomplete runs.
- `scripts/db-backup.sh` (new)
  - Added deterministic Postgres backup helper.
- `scripts/db-restore.sh` (new)
  - Added recreate-and-restore workflow helper.
- `scripts/phase6-drill.sh` (new)
  - Added RPO/RTO drill with table-parity verification and markdown report output.
- `scripts/phase6-demo.sh` (new)
  - Added one-command Phase 6 readiness workflow.
- `deploy/phase6-alert-thresholds.yaml` (new)
  - Added reference alert thresholds for operations summary metrics.
- `Makefile`
  - Added targets:
    - `db-backup`
    - `db-restore`
    - `phase6-drill`
    - `phase6-demo`

### Test changes

- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added:
    - `P6-01 readiness endpoint reports healthy postgres and object storage dependencies`
    - `P6-02 ops summary endpoint enforces authz and emits audit`
  - Added:
    - `P6-02` unauthorized/forbidden/authorized authz checks for `/v1/admin/ops/summary`.
    - audit assertion for `ops.summary.read`.

### Documentation changes

- `docs/22-phase6-implementation-tickets.md` (new)
  - Added Phase 6 ticket board and implementation notes.
- `docs/23-phase6-runbook.md` (new)
  - Added Phase 6 runbook for readiness/demo/drill workflows.
- `docs/24-security-review-closure.md` (new)
  - Added security closure checklist and launch blocker status.
- `docs/25-upgrade-rollback-runbook.md` (new)
  - Added deterministic upgrade and rollback procedure.
- Updated:
  - `README.md`
  - `docs/05-implementation-plan.md`
  - `docs/07-delivery-walkthrough-plan.md`
  - `docs/10-current-state.md`

### Updated verification

- `make build` passed.
- `make test` passed: `84` tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --configuration Debug --no-build -v minimal --filter "Category=Integration"` passed: `56` integration tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -nologo --configuration Debug --no-build -v minimal` passed: `140` total tests.
- `make format` passed.

## Track B MUT-07c / MUT-09 / MUT-10 Completion Addendum (2026-02-11, latest)

### Code and tooling changes

- `tools/Artifortress.MutationTrack/Program.fs`
  - Added `validate-fsharp-mutants` command for MUT-07c compile-validation.
  - Added safety guards to prevent destructive scratch-root deletion outside `artifacts/` and to enforce source-file containment under the target project directory.
  - Added lexical compile-validation candidate filtering to avoid non-expression F# tokens (for example type-definition `=` and generic-angle-bracket markers).
  - Added compile-validation markdown/json artifact writers.
  - Added parsed run metrics to mutation report output:
    - `candidateCount`
    - `mappedSpanCount`
    - `plannedMutantCount`
    - `createdMutantCount`
    - `quarantinedCompileErrorCount`
- `scripts/mutation-trackb-compile-validate.sh` (new)
  - Added one-command compile-validation gate for sampled `Library.fs` operator mutants.
  - Emits CI summary and metrics artifacts under `artifacts/ci/`.
- `scripts/mutation-trackb-assert.sh`
  - Added explicit invariant checks for candidate/mapped/planned/created/quarantine counts.
- `Makefile`
  - Added `mutation-trackb-compile-validate` target.
- `.github/workflows/mutation-track.yml`
  - Added compile-validation execution step.
  - Added upload of compile-validation report/json + CI summary/metrics artifacts.

### Latest Track B verification outcome

- `make mutation-trackb-spike` passed.
- `RUN_SPIKE=false make mutation-trackb-assert` passed with:
  - candidates: `71`
  - mapped spans: `71`
  - planned mutants: `71`
  - created mutants: `71`
  - quarantined compile errors: `71`
  - classification: `quarantined_compile_errors`
- `RUN_SPIKE=false make mutation-trackb-compile-validate` passed with:
  - discovered compile-validation candidates: `5`
  - selected mutants: `5`
  - compile successes: `5`
  - compile failures: `0`

### Documentation updates

- Updated:
  - `docs/32-fsharp-mutation-track-tickets.md`
  - `docs/33-fsharp-mutation-trackb-plan.md`
  - `docs/10-current-state.md`
  - `docs/05-implementation-plan.md`
  - `README.md`
- Added/updated reports:
  - `docs/reports/mutation-trackb-mut06-latest.md`
  - `docs/reports/mutation-trackb-mut07c-compile-validation.md`
