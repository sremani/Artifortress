# Consolidated Change Summary

Last updated: 2026-02-11

## Scope of This Change Set

This change set combines:
- Domain/API validation hardening for repository keys.
- Unit + integration regression coverage for the hardening path.
- FsCheck property-based testing expansion across domain and API helpers.
- Three extraction waves in the worker to expose pure internals for deeper property testing.
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

## Test Changes

- `tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj`
  - Added `PropertyTests.fs` compile include.
  - Added `FsCheck.Xunit` package reference.

- `tests/Artifortress.Domain.Tests/Tests.fs`
  - Added unit test for colon-rejecting repo keys.

- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`
  - Added integration test for create-repo colon rejection behavior.

- `tests/Artifortress.Domain.Tests/PropertyTests.fs` (new)
  - Expanded to `75` FsCheck properties covering:
    - domain role/scope/auth invariants,
    - API validation/parsing/range/digest invariants,
    - object storage config normalization,
    - extracted worker helper invariants across all three extraction waves.

## Documentation Changes

- `README.md`
  - Updated status and documentation map for worker extraction + PBT coverage.

- `docs/10-current-state.md`
  - Updated verification counts and extraction-wave notes.

- `docs/14-worker-pbt-extraction-tickets.md` (new)
  - Worker extraction ticket board and implementation notes.

- `docs/15-change-summary.md` (this file)
  - Consolidated summary of all changes in this set.

## Latest Verification

- `make format` passed.
- `make test` (non-integration filter) passed: `84` tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -nologo` passed: `122` tests.
