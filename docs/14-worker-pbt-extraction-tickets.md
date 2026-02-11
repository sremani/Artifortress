# Worker PBT Extraction Tickets

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: waiting on dependency or decision

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| W-PBT-01 | Extract pure worker helper modules for outbox parsing, retry backoff, and env parsing | Existing Phase 4 worker baseline | done |
| W-PBT-02 | Add property-based tests for extracted outbox version-id resolution logic | W-PBT-01 | done |
| W-PBT-03 | Add property-based tests for retry backoff schedule invariants | W-PBT-01 | done |
| W-PBT-04 | Add property-based tests for worker env integer parsing invariants | W-PBT-01 | done |
| W-PBT-05 | Integrate extraction notes into current-state docs and testing guide | W-PBT-02, W-PBT-03, W-PBT-04 | done |
| W-PBT-06 | Extract DB-adjacent worker flow helpers for outbox routing and job processing decisions | W-PBT-01 | done |
| W-PBT-07 | Extract DB parameter normalization helpers for claim/sweep query inputs | W-PBT-01 | done |
| W-PBT-08 | Rewire worker runtime to consume second-wave pure flow helpers | W-PBT-06, W-PBT-07 | done |
| W-PBT-09 | Add property-based tests for second-wave DB-adjacent flow helpers | W-PBT-06, W-PBT-07 | done |
| W-PBT-10 | Document second-wave extraction and validation outcomes | W-PBT-08, W-PBT-09 | done |
| W-PBT-11 | Extract SQL-row shape constructors for claimed outbox/job records | W-PBT-08 | done |
| W-PBT-12 | Extract sweep metric reducers for outbox/job outcome aggregation | W-PBT-08 | done |
| W-PBT-13 | Rewire sweep loops to use extracted row-shape and reducer helpers | W-PBT-11, W-PBT-12 | done |
| W-PBT-14 | Add property-based tests for third-wave row-shape and reducer helpers | W-PBT-11, W-PBT-12 | done |
| W-PBT-15 | Document third-wave extraction and validation outcomes | W-PBT-13, W-PBT-14 | done |

## W-PBT-01 Scope

- Introduce dedicated pure helper modules in `src/Artifortress.Worker` for:
  - outbox event version-id resolution (aggregate id + payload JSON)
  - retry backoff schedule calculation
  - environment integer parsing with defaults
- Rewire existing worker code paths to use these helpers.
- Preserve runtime behavior for normal inputs while improving testability.

Acceptance criteria:
- Worker runtime compiles and behaves equivalently for existing integration tests.
- Helper modules are callable from the test project without reflection.
- No behavior regression in `make test`.

## W-PBT-02 Scope

- Property tests for version-id resolution behavior:
  - aggregate id GUID fast path
  - JSON payload fallback path
  - malformed payload rejection invariants

## W-PBT-03 Scope

- Property tests for retry scheduling invariants:
  - monotonic non-decreasing backoff by attempt number
  - cap behavior at maximum exponent
  - deterministic offset from supplied reference time

## W-PBT-04 Scope

- Property tests for env parsing invariants:
  - valid positive integers pass through
  - non-positive/invalid/missing values fall back to defaults

## W-PBT-05 Scope

- Update project docs to reflect new extracted modules and PBT coverage.

## Implementation Notes (2026-02-11)

- W-PBT-01 completed:
  - Added `src/Artifortress.Worker/WorkerInternals.fs` with pure helper modules:
    - `WorkerOutboxParsing.tryResolveVersionId`
    - `WorkerRetryPolicy` (backoff and schedule helpers)
    - `WorkerEnvParsing.parsePositiveIntOrDefault`
  - Updated `src/Artifortress.Worker/Program.fs` to use extracted helpers in:
    - outbox sweep version-id resolution
    - job-failure retry scheduling
    - environment integer parsing for worker config
  - Updated compile order in `src/Artifortress.Worker/Artifortress.Worker.fsproj`.

- W-PBT-02 / W-PBT-03 / W-PBT-04 completed:
  - Added FsCheck properties in `tests/Artifortress.Domain.Tests/PropertyTests.fs` for:
    - outbox aggregate-id fast path and JSON fallback path invariants
    - malformed payload rejection behavior
    - retry backoff monotonicity/cap invariants and deterministic scheduling
    - worker env parsing pass-through and fallback invariants

- W-PBT-05 completed:
  - Added this ticket board and implementation log.
  - Updated `docs/10-current-state.md` verification and extraction notes.

- W-PBT-06 / W-PBT-07 / W-PBT-08 completed:
  - Added DB-adjacent pure helper modules in `src/Artifortress.Worker/WorkerInternals.fs`:
    - `WorkerDbParameters` for batch-size/max-attempt normalization.
    - `WorkerOutboxFlow` for enqueue-vs-requeue routing decisions.
    - `WorkerJobFlow` for complete-vs-fail decision planning with deterministic failure payload.
  - Rewired `src/Artifortress.Worker/Program.fs` to consume these modules in claim parameter binding and per-row sweep logic.

- W-PBT-09 completed:
  - Added FsCheck properties in `tests/Artifortress.Domain.Tests/PropertyTests.fs` for:
    - DB parameter normalization invariants.
    - outbox routing precedence and fallback behavior.
    - job processing decision determinism and failure-shape invariants.

- W-PBT-10 completed:
  - Updated this ticket board with second-wave implementation status.

- W-PBT-11 / W-PBT-12 / W-PBT-13 completed:
  - Added third-wave helper modules in `src/Artifortress.Worker/WorkerInternals.fs`:
    - `WorkerDataShapes` (claimed outbox/job row constructors).
    - `WorkerSweepMetrics` (pure reducers for outbox/job counters).
  - Rewired `src/Artifortress.Worker/Program.fs` sweep loops to consume:
    - `WorkerDataShapes.createClaimedOutboxEvent`
    - `WorkerDataShapes.createClaimedSearchJob`
    - `WorkerSweepMetrics.recordRequeue`
    - `WorkerSweepMetrics.recordEnqueue`
    - `WorkerSweepMetrics.recordCompleted`
    - `WorkerSweepMetrics.recordFailed`

- W-PBT-14 completed:
  - Added FsCheck properties in `tests/Artifortress.Domain.Tests/PropertyTests.fs` for:
    - row-shape constructor field-preservation invariants.
    - outbox/job sweep reducer increment and fold invariants.

- W-PBT-15 completed:
  - Updated this ticket board with third-wave implementation status.

## Validation Snapshot (2026-02-11)

- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -nologo` -> `122` passed, `0` failed.
- `make format` -> passed.
- `make test` (non-integration) -> `84` passed, `0` failed.
- Property-based coverage count in `tests/Artifortress.Domain.Tests/PropertyTests.fs` -> `75` properties.
