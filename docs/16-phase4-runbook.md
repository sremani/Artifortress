# Phase 4 Demo Runbook

This runbook demonstrates Phase 4 behaviors for policy evaluation, quarantine lifecycle, and search pipeline fallback handling.

## Prerequisites

- .NET SDK 10.0.102+ (`dotnet --version`)
- Docker with Compose running
- Local database/storage dependencies started
- Repository built locally

## One-time setup

```bash
make dev-up
make db-migrate
make build
```

## Execute demo

```bash
./scripts/phase4-demo.sh
```

Equivalent make target:

```bash
make phase4-demo
```

## What the demo validates

- Policy `deny` path is deterministic and does not create quarantine side effects.
- Policy `quarantine` path creates quarantine state and supports deterministic release.
- Search pipeline happy path:
  - outbox `version.published` event is consumed,
  - search job is created and completed for a published version.
- Search pipeline degraded/fallback paths:
  - unpublished version search job fails with deterministic error (`version_not_published`),
  - malformed outbox event is requeued and remains undelivered.
- Policy/quarantine correctness remains intact while degraded search artifacts exist.
- Audit endpoint contains policy/quarantine mutation events.

## Environment variables

Optional overrides:

- `API_URL` (default: `http://127.0.0.1:8086`)
- `CONNECTION_STRING` (default local Postgres connection)
- `BOOTSTRAP_TOKEN` (default: `phase4-demo-bootstrap`)
- `POSTGRES_USER` (default: `artifortress`)
- `POSTGRES_DB` (default: `artifortress`)

Example:

```bash
BOOTSTRAP_TOKEN=my-bootstrap-token API_URL=http://127.0.0.1:8087 ./scripts/phase4-demo.sh
```

## Troubleshooting

- If API does not become healthy:
  - inspect `/tmp/artifortress-phase4-demo-api.log`
- If worker validations fail:
  - inspect `/tmp/artifortress-phase4-demo-worker.log`
- If SQL operations fail:
  - ensure Postgres container is up (`make dev-up`)
  - ensure migrations are current (`make db-migrate`)
- If auth calls fail unexpectedly:
  - verify `BOOTSTRAP_TOKEN` passed to script matches API bootstrap token.
