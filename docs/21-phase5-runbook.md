# Phase 5 Demo Runbook

This runbook demonstrates Phase 5 deletion lifecycle behavior: tombstoning, GC dry-run/execute, and reconcile drift reporting.

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

## Execute demo

```bash
./scripts/phase5-demo.sh
```

Equivalent make target:

```bash
make phase5-demo
```

## What the demo validates

- Tombstone transition from published version to `tombstoned` state.
- Retention expiry simulation and safe GC dry-run behavior.
- GC execute path hard-deletes expired tombstoned versions and orphan blobs.
- Upload-session blob references are released before blob deletion.
- Reconcile endpoint returns drift summary payload.
- Audit endpoint includes lifecycle actions:
  - `package.version.tombstoned`
  - `gc.run.dry_run`
  - `gc.run.execute`
  - `reconcile.blobs.checked`

## Environment variables

Optional overrides:

- `API_URL` (default: `http://127.0.0.1:8086`)
- `CONNECTION_STRING` (default local Postgres connection)
- `BOOTSTRAP_TOKEN` (default: `phase5-demo-bootstrap`)
- `POSTGRES_USER` (default: `artifortress`)
- `POSTGRES_DB` (default: `artifortress`)

Example:

```bash
BOOTSTRAP_TOKEN=my-bootstrap-token API_URL=http://127.0.0.1:8087 ./scripts/phase5-demo.sh
```

## Troubleshooting

- If API does not become healthy:
  - inspect `/tmp/artifortress-phase5-demo-api.log`
- If SQL checks fail:
  - ensure Postgres container is running (`make dev-up`)
  - ensure migrations are current (`make db-migrate`)
- If auth calls fail unexpectedly:
  - verify `BOOTSTRAP_TOKEN` matches API bootstrap token.
- If blob lifecycle checks fail:
  - rerun with a fresh local DB (`make dev-down && make dev-up && make db-migrate`).
