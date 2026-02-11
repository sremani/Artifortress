# Phase 5 Implementation Tickets

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| P5-01 | Tombstone API with retention window | Phase 4 baseline | done |
| P5-02 | GC run API with safe dry-run mode | P5-01 | done |
| P5-03 | Execute GC hard-delete flow for expired tombstones | P5-02 | done |
| P5-04 | Reconcile API for metadata/blob drift summary | P5-02 | done |
| P5-05 | Admin authz + audit coverage for lifecycle endpoints | P5-01, P5-02, P5-04 | done |
| P5-06 | Blob-delete FK handling for upload-session references | P5-03 | done |
| P5-07 | Integration tests for tombstone/gc/reconcile paths | P5-01 through P5-06 | done |
| P5-08 | Phase 5 demo script + runbook updates | P5-07 | done |

## Current Implementation Notes (2026-02-11)

- P5-01 completed:
  - Added migration `db/migrations/0008_phase5_tombstones_gc_reconcile.sql` with:
    - `tombstones` table + retention index.
    - `gc_runs` + `gc_marks` tables for mark-and-sweep observability.
  - Added tombstone API:
    - `POST /v1/repos/{repoKey}/packages/versions/{versionId}/tombstone`
  - Added deterministic state handling:
    - supports tombstoning from `draft|published`.
    - returns conflict for illegal states.
    - supports idempotent repeated tombstone requests.
  - Added audit action:
    - `package.version.tombstoned`.
- P5-02 completed:
  - Added GC run API:
    - `POST /v1/admin/gc/runs`
  - Added default lifecycle config keys:
    - `Lifecycle:DefaultTombstoneRetentionDays` (default `30`)
    - `Lifecycle:DefaultGcRetentionGraceHours` (default `24`)
    - `Lifecycle:DefaultGcBatchSize` (default `200`)
  - Implemented dry-run safety semantics:
    - dry-run does not mutate package or blob rows.
    - execute mode performs deletions.
- P5-03 completed:
  - Implemented GC execute flow:
    - marks live digests from non-expired metadata roots.
    - hard-deletes expired tombstoned versions in bounded batches.
    - deletes unreferenced blobs with object-store delete attempt handling (`NotFound` tolerated).
  - Persists GC run metrics (`marked`, `candidate`, `deleted`, `errors`) in `gc_runs`.
- P5-04 completed:
  - Added reconcile API:
    - `GET /v1/admin/reconcile/blobs?limit={n}`
  - Reports drift summary:
    - missing artifact blob refs.
    - missing manifest blob refs.
    - orphan blob count + bounded sample arrays.
  - Added audit action:
    - `reconcile.blobs.checked`.
- P5-05 completed:
  - Enforced admin role checks for `/v1/admin/gc/runs` and `/v1/admin/reconcile/blobs`.
  - Added GC audit actions:
    - `gc.run.dry_run`
    - `gc.run.execute`
- P5-06 completed:
  - Added GC pre-delete cleanup of `upload_sessions.committed_blob_digest` references.
  - Updated migration 0008 FK behavior to `ON DELETE SET NULL` for `upload_sessions_committed_blob_digest_fkey`.
- P5-07 completed:
  - Added integration tests in `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`:
    - `P5-01` tombstone authz + transition + idempotency.
    - `P5-02` GC dry-run safety and candidate reporting.
    - `P5-03` GC execute hard-delete for expired tombstones + orphan blobs.
    - `P5-04` reconcile drift summary endpoint.
    - `P5-05` admin endpoint unauthorized/forbidden/authorized matrix.
- P5-08 completed:
  - Added executable demo script:
    - `scripts/phase5-demo.sh`
  - Added runbook:
    - `docs/21-phase5-runbook.md`
  - Added make target:
    - `make phase5-demo`

Latest local verification:
- `make build`
- `make test`
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --configuration Debug --no-build -v minimal --filter "Category=Integration"` (`53` passing integration tests)
- `make format`

## Ticket Details

## P5-01: Tombstone API with retention window

Scope:
- Add a version-level tombstone API that records reason, actor, and retention window.
- Preserve deterministic lifecycle transitions and idempotency behavior.

Acceptance criteria:
- Tombstone transition is auditable and permission-gated.
- Repeated tombstone calls are deterministic and safe.

## P5-02: GC run API with safe dry-run mode

Scope:
- Add admin API to run mark-and-sweep in dry-run or execute mode.
- Support bounded run inputs (`retentionGraceHours`, `batchSize`).

Acceptance criteria:
- Dry-run mode does not mutate version/blob state.
- Execute mode returns deterministic run metrics.

## P5-03: Execute GC hard-delete flow for expired tombstones

Scope:
- Hard-delete versions after retention expiry.
- Remove orphaned blob metadata/object references in bounded batches.

Acceptance criteria:
- Expired tombstoned versions are removed safely.
- Blob deletion path is resilient to object-store missing-object responses.

## P5-04: Reconcile API for metadata/blob drift summary

Scope:
- Add read-only reconcile endpoint for metadata->blob and blob->metadata drift checks.
- Provide bounded sample outputs for operator triage.

Acceptance criteria:
- Reconcile payload includes counts and sample arrays.
- Endpoint is admin-protected and audited.

## P5-05: Admin authz + audit coverage for lifecycle endpoints

Scope:
- Enforce unauthorized/forbidden paths on new admin lifecycle APIs.
- Emit audit events for tombstone/GC/reconcile mutations and checks.

Acceptance criteria:
- Unauthorized/forbidden responses are deterministic.
- Audit records exist for lifecycle actions.

## P5-06: Blob-delete FK handling for upload-session references

Scope:
- Ensure GC blob deletion does not violate upload-session FK constraints.
- Keep upload-session history while allowing blob hard-delete.

Acceptance criteria:
- GC execute does not fail with FK violations.
- upload session references are safely released/nullified.

## P5-07: Integration tests for tombstone/gc/reconcile paths

Scope:
- Add integration coverage for tombstone lifecycle, GC dry-run/execute, reconcile summary, and admin authz.

Acceptance criteria:
- CI fails on lifecycle regressions.
- Dry-run safety and execute hard-delete behaviors are asserted.

## P5-08: Phase 5 demo script + runbook updates

Scope:
- Add executable script covering tombstone, GC dry-run/execute, reconcile, and audit checks.
- Document setup, execution, and troubleshooting.

Acceptance criteria:
- `make phase5-demo` demonstrates Phase 5 lifecycle end-to-end.
- Runbook includes expected validations and troubleshooting guidance.
