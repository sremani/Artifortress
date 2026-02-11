# Phase 6 Runbook

This runbook demonstrates Phase 6 hardening and GA-readiness workflows:
- dependency-backed readiness checks,
- SLO posture via operations summary,
- backup/restore and RPO/RTO drill,
- launch-readiness report generation.

## Prerequisites

- .NET SDK 10.0.102+ (`dotnet --version`)
- Docker with Compose running
- Local Postgres and MinIO dependencies available
- Repository built locally

## One-time setup

```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
```

## Core commands

Run full Phase 6 readiness demo:

```bash
make phase6-demo
```

Run DR drill only:

```bash
make phase6-drill
```

Create backup snapshot:

```bash
make db-backup
```

Restore from backup snapshot:

```bash
RESTORE_PATH=/tmp/artifortress-backup.sql make db-restore
```

## What `phase6-demo` validates

- `make test` and `make format` pass.
- backup/restore drill passes with table parity checks.
- `/health/ready` returns `ready` with healthy dependency entries.
- `/v1/admin/ops/summary` returns admin-gated backlog snapshot.
- reports are generated:
  - `docs/reports/phase6-rto-rpo-drill-latest.md`
  - `docs/reports/phase6-ga-readiness-latest.md`

## SLO/alert thresholds

Reference thresholds:
- `deploy/phase6-alert-thresholds.yaml`

Primary indicators from `/v1/admin/ops/summary`:
- `pendingOutboxEvents`
- `availableOutboxEvents`
- `oldestPendingOutboxAgeSeconds`
- `failedSearchJobs`
- `pendingSearchJobs`
- `incompleteGcRuns`
- `recentPolicyTimeouts24h`

## Environment variables

`phase6-demo` supports:
- `API_URL` (default `http://127.0.0.1:8086`)
- `CONNECTION_STRING` (default local Postgres)
- `BOOTSTRAP_TOKEN` (default `phase6-demo-bootstrap`)
- `REPORT_PATH` (default `docs/reports/phase6-ga-readiness-latest.md`)

`phase6-drill` supports:
- `POSTGRES_USER` (default `artifortress`)
- `POSTGRES_DB` (default `artifortress`)
- `DRILL_DB` (default `artifortress_drill`)
- `BACKUP_PATH` (default generated under `/tmp`)
- `REPORT_PATH` (default `docs/reports/phase6-rto-rpo-drill-latest.md`)
- `RTO_TARGET_SECONDS` (default `900`)
- `RPO_TARGET_SECONDS` (default `300`)

## Troubleshooting

- If API does not become healthy:
  - inspect `/tmp/artifortress-phase6-demo-api.log`
- If backup/restore drill fails:
  - verify Postgres container is running (`make dev-up`)
  - ensure migrations are current (`make db-migrate`)
  - inspect generated report for mismatch details
- If ops summary returns `403`:
  - confirm the caller token has `repo:*:admin` scope
