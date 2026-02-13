# Consolidated Change Summary

Last updated: 2026-02-12

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

## Native F# Mutation Runtime Pivot Addendum (2026-02-11, latest)

### Strategic pivot

- Primary side-quest execution path now favors F#-first native runtime execution instead of waiting on Stryker-fork runtime activation maturity.

### Code and tooling changes

- `tools/Artifortress.MutationTrack/Program.fs`
  - Added `run-fsharp-native` command.
  - Added native runtime status model:
    - `killed`
    - `survived`
    - `compile_error`
    - `infrastructure_error`
  - Added native runtime markdown/json report generation.
  - Reused safety guards for scratch-root and workspace/source-path containment.
- `scripts/mutation-fsharp-native-run.sh` (new)
  - Added one-command native runtime lane runner + CI summary/metrics outputs.
- `Makefile`
  - Added `mutation-fsharp-native` target.
- `docs/34-fsharp-native-mutation-tickets.md` (new)
  - Added F#-native side-quest finish ticket board.

### Validation outcome

- `MAX_MUTANTS=3 make mutation-fsharp-native` passed with:
  - selected mutants: `3`
  - killed: `2`
  - survived: `1`
  - compile errors: `0`
  - infrastructure errors: `0`
- This confirms real runtime-executed mutant statuses are now available in-repo without depending on fork runtime activation.

## Native Mutation CI/Score Completion Addendum (2026-02-11, latest)

### Code and workflow changes

- `.github/workflows/mutation-track.yml`
  - Added non-blocking `mutation-native` job that runs:
    - `MAX_MUTANTS=6 make mutation-fsharp-native`
    - `MIN_MUTATION_SCORE=40 make mutation-fsharp-native-score`
  - Added native artifact upload bundle.
  - Added promotion toggle support via GitHub variables:
    - `MUTATION_NATIVE_ENFORCE`
    - `MUTATION_NATIVE_MAX_MUTANTS`
    - `MUTATION_NATIVE_MIN_SCORE`
- `tools/Artifortress.MutationTrack/Program.fs`
  - Expanded safe candidate support to include `=` mutations in guarded boolean-expression contexts.
- `scripts/mutation-fsharp-native-score.sh` (new)
  - Added native mutation score computation and threshold gate (`MIN_MUTATION_SCORE`).
  - Emits markdown + CI summary + JSON metrics artifacts.
- `Makefile`
  - Added `mutation-fsharp-native-score` target.
- `docs/35-native-mutation-gate-promotion.md` (new)
  - Added promotion criteria and operational rollout/rollback guide for enabling blocking mutation gate mode.

### Ticket outcomes

- `MUTN-05`: done (native CI lane added).
- `MUTN-06`: done (safe rewrite set expanded; higher selected mutant count with compile safety retained).
- `MUTN-07`: done (score policy/reporting implemented).

### Validation evidence

- `MAX_MUTANTS=6 make mutation-fsharp-native`:
  - selected: `6`
  - killed: `5`
  - survived: `1`
  - compile errors: `0`
  - infrastructure errors: `0`
- `MIN_MUTATION_SCORE=40 make mutation-fsharp-native-score`:
  - score: `83.33`
  - threshold met: `true`

## Native Mutation Trend Retention Addendum (2026-02-11, latest)

### Code and workflow changes

- `tools/Artifortress.MutationTrack/Program.fs`
  - Switched native and compile-validation scratch handling to per-run subdirectories to avoid cross-run delete races (`Directory not empty`) in repeated local/CI execution.
- `scripts/mutation-fsharp-native-trend.sh` (new)
  - Added score-history append and trend report generation using score summary artifacts.
- `Makefile`
  - Added `mutation-fsharp-native-trend` target.
- `.github/workflows/mutation-track.yml`
  - Added cache restore/save steps for `artifacts/mutation/mutation-native-score-history.csv`.
  - Added trend generation step and trend artifact uploads.

### Validation evidence

- `MAX_MUTANTS=6 make mutation-fsharp-native` passed.
- `MIN_MUTATION_SCORE=40 make mutation-fsharp-native-score` passed.
- `make mutation-fsharp-native-trend` passed with cumulative history rows.

## Native Mutation Burn-in Signal Addendum (2026-02-11, latest)

### Code and workflow changes

- `scripts/mutation-fsharp-native-burnin.sh` (new)
  - Added streak-based readiness evaluation over `artifacts/mutation/mutation-native-score-history.csv`.
  - Enforces per-run pass conditions:
    - `compile_error_count == 0`
    - `infrastructure_error_count == 0`
    - `threshold_met == true`
    - `score_percent >= MIN_MUTATION_SCORE`
  - Emits:
    - `docs/reports/mutation-native-burnin-latest.md`
    - `artifacts/ci/mutation-native-burnin-summary.txt`
    - `artifacts/ci/mutation-native-burnin.json`
- `Makefile`
  - Added `mutation-fsharp-native-burnin` target.
- `.github/workflows/mutation-track.yml`
  - Added `MUTATION_NATIVE_REQUIRED_STREAK` variable input.
  - Added burn-in evaluation step and artifact uploads.
  - Added enforce-time burn-in interlock:
    - when `MUTATION_NATIVE_ENFORCE=true`, burn-in readiness is required or CI fails.
- `docs/34-fsharp-native-mutation-tickets.md`
  - Marked `MUTN-08` as `partial` (time-window dependent).
  - Added `MUTN-10` as done for burn-in readiness artifact support.
  - Added `MUTN-11` as done for enforce-time readiness interlock.

### Validation evidence

- `REQUIRED_STREAK=7 MIN_MUTATION_SCORE=40 make mutation-fsharp-native-burnin` passed.
- Current expected status in local artifacts remains `burnin_ready=false` when history depth is below required streak.

## Lifecycle + Policy/Search Coverage Expansion Addendum (2026-02-11, latest)

### Test coverage changes

- `tests/Artifortress.Domain.Tests/PropertyTests.fs`
  - Added lifecycle request validation properties for:
    - `validateTombstoneRequest` defaulting, range handling, and normalization behavior.
    - `validateGcRequest` defaulting, batch fallback, and range guard behavior.
  - Added `7` new FsCheck properties.
- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added policy/search mixed-routing stress test:
    - `P4-09 search outbox sweep handles mixed routing and idempotent job upserts`.
    - Validates aggregate-id + payload fallback routing, duplicate event idempotent upsert behavior, and malformed-event requeue behavior in one flow.
  - Added lifecycle selective-GC stress test:
    - `P5-07 GC execute deletes expired tombstones while preserving retained tombstones`.
    - Validates selective deletion boundaries for expired vs non-expired tombstones plus orphan cleanup.

### Documentation updates

- Updated:
  - `docs/10-current-state.md`
  - `docs/13-phase4-implementation-tickets.md`
  - `docs/20-phase5-implementation-tickets.md`

### Validation evidence

- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category!=Integration" -v minimal` passed (`91` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category=Integration" -v minimal` passed (`58` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` passed (`149` tests total).
- `make format` passed.

## Lifecycle + Policy/Search Stress Wave 2 Addendum (2026-02-11, latest)

### Test coverage changes

- `tests/Artifortress.Domain.Tests/PropertyTests.fs`
  - Added GC boundary acceptance property:
    - `validateGcRequest` accepts boundary values for `retentionGraceHours` (`0`/`8760`) and `batchSize` (`1`/`5000`).
  - Property count increased to `83`.
- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added policy/search stress tests:
    - `P4-stress quarantine list filters remain consistent under mixed status transitions`.
    - `P4-stress search sweeps honor batch limits across backlog`.
  - Added lifecycle stress test:
    - `P5-stress GC execute honors batch size across multiple runs for expired tombstones`.
  - Added fixture helper:
    - `SetTombstoneRetention(versionId, retentionUntilUtc)` for deterministic retention control in lifecycle stress scenarios.

### Validation evidence

- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category!=Integration" -v minimal` passed (`92` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category=Integration" -v minimal` passed (`61` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` passed (`153` total tests).
- `make format` passed.

## Lifecycle + Policy/Search Stress Wave 3 Addendum (2026-02-11, latest)

### Test coverage changes

- `tests/Artifortress.Domain.Tests/PropertyTests.fs`
  - Added tombstone validation property:
    - `validateTombstoneRequest` rejects blank reasons.
  - Property count increased to `84`.
- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added policy/search repo-isolation stress test:
    - `P4-stress quarantine state remains repo-scoped under concurrent repos`.
  - Added lifecycle orphan-volume stress test:
    - `P5-stress reconcile sample limit and GC blob batch drain are deterministic under orphan volume`.
  - Existing wave-2 stress tests retained and validated in full-suite context.

### Validation evidence

- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category!=Integration" -v minimal` passed (`93` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category=Integration" -v minimal` passed (`63` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` passed (`156` total tests).
- `make format` passed.

## Lifecycle + Policy/Search Stress Wave 4 Addendum (2026-02-11, latest)

### Test coverage changes

- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added search retry-window stress test:
    - `P4-stress failed search job is deferred by backoff and not immediately reclaimed`.
  - Added lifecycle retention-grace stress test:
    - `P5-stress GC retention grace excludes fresh orphan blobs until grace window is reduced`.
  - Added fixture helpers for deterministic stress orchestration:
    - `SetBlobCreatedAt(digest, createdAtUtc)`
    - `TryReadSearchIndexJobSchedule(versionId)`
- `tests/Artifortress.Domain.Tests/PropertyTests.fs`
  - Added tombstone request invariant:
    - blank `reason` is rejected deterministically.

### Validation evidence

- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category!=Integration" -v minimal` passed (`93` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category=Integration" -v minimal` passed (`65` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` passed (`158` total tests).
- `make format` passed.

## Lifecycle + Policy/Search Stress Wave 5 Addendum (2026-02-11, latest)

### Test coverage changes

- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added outbox event-type filtering stress test:
    - `P4-stress outbox sweep ignores non-version-published events under mixed backlog`.
  - Added shared-digest repo-isolation stress test:
    - `P4-stress quarantined shared digest in one repo does not block download in another repo`.
    - Includes dedupe-aware upload-path handling (`201 initiated` vs `200 committed`).
  - Added lifecycle default behavior stress test:
    - `P5-stress GC defaults to dry-run when request body is omitted`.

### Validation evidence

- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category!=Integration" -v minimal` passed (`93` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category=Integration" -v minimal` passed (`68` tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` passed (`161` total tests).
- `make format` passed.

## Lifecycle + Policy/Search Stress Wave 6 Addendum (2026-02-12, latest)

### Test coverage changes

- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added operations-summary outbox stress test:
    - `P6-stress ops summary outbox counters separate pending and available via deterministic deltas`.
    - Verifies pending-vs-available separation and delivered-event exclusion via baseline/delta assertions.
  - Added operations-summary search-job stress test:
    - `P6-stress ops summary search job status counters track pending processing and failed deltas`.
    - Verifies `pending + processing` roll-up and `failed` counters with deterministic fixture-driven status setup.
  - Added fixture helpers for deterministic state control:
    - `SetOutboxEventStateForTest(eventId, availableAtUtc, occurredAtUtc, deliveredAtUtc)`.
    - `UpsertSearchIndexJobForVersionForTest(versionId, status, attempts, availableAtUtc, lastError)`.

### Documentation updates

- Updated:
  - `docs/10-current-state.md`
  - `docs/20-phase5-implementation-tickets.md`
  - `docs/22-phase6-implementation-tickets.md`

### Validation evidence

- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "FullyQualifiedName~P6-stress ops summary"` passed (`2` tests).
- `make test-integration` passed (`70` integration tests).
- `make test` passed (`93` non-integration tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` passed (`163` total tests).
- `make format` passed.

## Phase 7 Identity Track Kickoff Addendum (2026-02-12, latest)

### Code changes

- `src/Artifortress.Api/Program.fs`
  - Added OIDC validation configuration model:
    - `Auth:Oidc:Enabled`
    - `Auth:Oidc:Issuer`
    - `Auth:Oidc:Audience`
    - `Auth:Oidc:Hs256SharedSecret`
  - Added JWT validation path for OIDC bearer tokens (HS256 foundation mode):
    - signature validation,
    - issuer/audience/expiry/not-before claim checks,
    - scope extraction from `scope`, `scp`, and `artifortress_scopes` claims.
  - Added PAT-first auth fallback model:
    - token is first resolved as PAT,
    - then evaluated as OIDC when OIDC is enabled.
  - Added principal auth-source tagging (`pat` / `oidc`) and exposed it via `/v1/auth/whoami`.
  - Added SAML configuration scaffolding with explicit guard:
    - startup fails if `Auth:Saml:Enabled=true` because SAML validation flow is not implemented yet.

### Test changes

- `tests/Artifortress.Domain.Tests/Tests.fs`
  - Added unit tests for OIDC token validation:
    - accepts valid HS256 token with matching issuer/audience/scope claims.
    - rejects mismatched issuer.
    - rejects expired token.
- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added Phase 7 OIDC integration tests:
    - `P7-02 oidc bearer flow supports whoami and admin-scoped repo create`.
    - `P7-02 oidc token with mismatched audience is unauthorized`.

### Documentation changes

- Added:
  - `docs/36-phase7-identity-integration-tickets.md`
  - `docs/37-phase7-runbook.md`
- Updated:
  - `README.md`
  - `docs/05-implementation-plan.md`
  - `docs/10-current-state.md`
  - `docs/28-deployment-howto-staging.md`
  - `docs/29-deployment-howto-production.md`
  - `docs/30-deployment-config-reference.md`
  - `docs/31-operations-howto.md`
  - `deploy/staging-api.env.example`
  - `deploy/production-api.env.example`
  - `Makefile`
- Added executable demo flow:
  - `scripts/phase7-demo.sh`
  - `make phase7-demo`

### Validation evidence

- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "OIDC token validation"` passed (`3` tests).
- `make test` passed (`96` non-integration tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "FullyQualifiedName~P7-02"` passed (`2` tests).
- `make test-integration` passed (`73` integration tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` passed (`169` total tests).
- `make format` passed.

## Phase 7 Identity Completion Addendum (2026-02-13, latest)

### Code changes

- `src/Artifortress.Api/Program.fs`
  - Completed OIDC RS256/JWKS path:
    - added JWKS parser and RS256 signature verification.
    - added `kid` key selection with multi-key rotation support.
    - added mixed signing-mode configuration (`Hs256SharedSecret` and/or `JwksJson`).
  - Added OIDC claim-role mapping policy:
    - config key: `Auth:Oidc:RoleMappings`
    - mapping format: `claimName|claimValue|repoKey|role`
    - merged mapped scopes with direct scope claims.
  - Completed SAML ACS validation flow:
    - metadata endpoint: `GET /v1/auth/saml/metadata`
    - ACS endpoint: `POST /v1/auth/saml/acs`
    - SAML XML parsing and validation (issuer, audience, subject).
    - SAML claim-role mapping policy using same mapping format.
    - scoped token issuance and audit action `auth.saml.exchange`.
  - Added Phase 7 config expansion:
    - `Auth:Oidc:JwksJson`
    - `Auth:Oidc:RoleMappings`
    - `Auth:Saml:ExpectedIssuer`
    - `Auth:Saml:RoleMappings`
    - `Auth:Saml:IssuedPatTtlMinutes`

### Test changes

- `tests/Artifortress.Domain.Tests/Tests.fs`
  - Added OIDC RS256/JWKS unit coverage:
    - valid RS256 token acceptance.
    - key-rotation behavior across multiple keys.
  - Added OIDC claim-role mapping unit coverage when direct scope claim is absent.
- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added Phase 7 integration coverage:
    - `P7-03 oidc rs256 bearer flow supports whoami and repo create`
    - `P7-03 oidc rs256 token with unknown kid is unauthorized`
    - `P7-04 oidc claim-role mapping grants wildcard admin from groups claim`
    - `P7-04 oidc claim-role mapping denies unmatched group claims`
    - `P7-05 saml metadata endpoint returns service-provider metadata contract`
    - `P7-05 saml acs endpoint rejects invalid base64 payload`
    - `P7-06 saml acs exchange validates assertion and returns scoped bearer token`
    - `P7-06 saml acs exchange rejects assertion with mismatched issuer`

### Documentation and runbook changes

- Updated:
  - `docs/05-implementation-plan.md`
  - `docs/10-current-state.md`
  - `docs/30-deployment-config-reference.md`
  - `docs/31-operations-howto.md`
  - `docs/36-phase7-identity-integration-tickets.md`
  - `docs/37-phase7-runbook.md`
  - `README.md`
  - `deploy/staging-api.env.example`
  - `deploy/production-api.env.example`
- Expanded demo:
  - `scripts/phase7-demo.sh` now validates OIDC direct and mapped auth, SAML metadata/ACS exchange, and PAT fallback.

### Validation evidence

- `make format` passed.
- `make test` passed (`99` non-integration tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category=Integration" -v minimal` passed (`81` integration tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "FullyQualifiedName~P7-0" -v minimal` passed (`11` Phase 7 tests).
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` passed (`180` total tests).
