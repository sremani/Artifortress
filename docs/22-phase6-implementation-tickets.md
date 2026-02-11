# Phase 6 Implementation Tickets

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| P6-01 | Readiness hardening with real dependency checks | Phase 5 baseline | done |
| P6-02 | Admin operations summary endpoint for backlog/SLO posture | P6-01 | done |
| P6-03 | Integration authz/audit coverage for Phase 6 admin and readiness APIs | P6-02 | done |
| P6-04 | Database backup script for operational recovery | P6-01 | done |
| P6-05 | Database restore script for disaster recovery workflow | P6-04 | done |
| P6-06 | Automated RPO/RTO drill script with verification report | P6-04, P6-05 | done |
| P6-07 | GA readiness demo script (checks + drill + ops snapshot) | P6-01 through P6-06 | done |
| P6-08 | SLO/alert threshold documentation and operations runbook | P6-02, P6-07 | done |
| P6-09 | Security review closure checklist and launch blockers log | P6-08 | done |
| P6-10 | Upgrade and rollback procedures runbook | P6-08 | done |

## Current Implementation Notes (2026-02-11)

- P6-01 completed:
  - Hardened `/health/ready` to perform real dependency checks:
    - Postgres SQL probe.
    - Object storage availability probe via bucket list call.
  - Readiness response now returns dependency status details and emits `503` when not ready.
- P6-02 completed:
  - Added operations summary API:
    - `GET /v1/admin/ops/summary`
  - Summary includes:
    - outbox pending/available counts
    - oldest pending outbox age seconds
    - search job failed/pending counts
    - incomplete GC runs count
    - last-24h policy timeout audit count
  - Added audit action:
    - `ops.summary.read`
- P6-03 completed:
  - Added integration tests in `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`:
    - `P6-01 readiness endpoint reports healthy postgres and object storage dependencies`
    - `P6-02 ops summary endpoint enforces authz and emits audit`
- P6-04/P6-05 completed:
  - Added scripts:
    - `scripts/db-backup.sh`
    - `scripts/db-restore.sh`
  - Added make targets:
    - `make db-backup`
    - `make db-restore`
- P6-06 completed:
  - Added `scripts/phase6-drill.sh`:
    - executes backup + restore into drill DB
    - validates required table row-count parity
    - enforces configurable RPO/RTO targets
    - emits report: `docs/reports/phase6-rto-rpo-drill-latest.md`
  - Added make target:
    - `make phase6-drill`
- P6-07 completed:
  - Added `scripts/phase6-demo.sh`:
    - runs static checks (`make test`, `make format`)
    - runs RPO/RTO drill
    - validates `/health/ready` and `/v1/admin/ops/summary`
    - emits report: `docs/reports/phase6-ga-readiness-latest.md`
  - Added make target:
    - `make phase6-demo`
- P6-08/P6-09/P6-10 completed:
  - Added runbooks/docs:
    - `docs/23-phase6-runbook.md`
    - `docs/24-security-review-closure.md`
    - `docs/25-upgrade-rollback-runbook.md`
  - Added alert-threshold config artifact:
    - `deploy/phase6-alert-thresholds.yaml`

Latest local verification:
- `make build`
- `make test`
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --configuration Debug --no-build -v minimal --filter "Category=Integration"` (`56` passing integration tests)
- `make format`

## Ticket Details

## P6-01: Readiness hardening with real dependency checks

Scope:
- Replace static readiness payload with runtime dependency probes.
- Emit deterministic non-ready responses when dependencies fail.

Acceptance criteria:
- `/health/ready` reflects actual dependency health.
- readiness errors are observable in response payload.

## P6-02: Admin operations summary endpoint for backlog/SLO posture

Scope:
- Add read-only operations summary endpoint for backlog/risk indicators.
- Include outbox/search/GC and policy-timeout signals.

Acceptance criteria:
- Admin-only endpoint returns deterministic summary schema.
- Access is audited.

## P6-03: Integration authz/audit coverage for Phase 6 admin and readiness APIs

Scope:
- Add integration tests for readiness and operations summary authz/audit paths.

Acceptance criteria:
- Unauthorized/forbidden/authorized paths are covered.
- Audit event assertions exist for ops summary reads.

## P6-04: Database backup script for operational recovery

Scope:
- Add script to take deterministic Postgres backup snapshots.

Acceptance criteria:
- Script emits non-empty backup file and fails fast on errors.

## P6-05: Database restore script for disaster recovery workflow

Scope:
- Add script to recreate and restore target DB from backup file.

Acceptance criteria:
- Script handles active-connection termination and restore execution safely.

## P6-06: Automated RPO/RTO drill script with verification report

Scope:
- Automate backup/restore drill and verify critical table parity.
- Produce repeatable markdown report artifact.

Acceptance criteria:
- Drill exits non-zero on parity/target failures.
- Report includes durations and pass/fail outcomes.

## P6-07: GA readiness demo script (checks + drill + ops snapshot)

Scope:
- Provide one command to run core launch-readiness checks.

Acceptance criteria:
- Script validates readiness endpoint, ops summary endpoint, and DR drill.
- Report artifact is generated.

## P6-08: SLO/alert threshold documentation and operations runbook

Scope:
- Document SLO indicators, thresholds, and operator actions.

Acceptance criteria:
- On-call has deterministic thresholds and response steps.

## P6-09: Security review closure checklist and launch blockers log

Scope:
- Record threat-model closure status and remaining launch blockers.

Acceptance criteria:
- No open P0/P1 blockers for launch sign-off.

## P6-10: Upgrade and rollback procedures runbook

Scope:
- Define production upgrade and rollback sequence with gates.

Acceptance criteria:
- Procedure is executable and includes validation checkpoints.
