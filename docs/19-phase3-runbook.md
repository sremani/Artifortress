# Phase 3 Demo Runbook

This runbook demonstrates the Phase 3 package publish workflow:

- draft creation
- artifact entry persistence
- manifest persistence + query
- atomic publish transition
- outbox emission idempotency
- publish workflow audit traces

## Prerequisites

- .NET SDK 10.0.102+ (`dotnet --version`)
- Docker with Compose running
- Local Postgres available through `docker compose`
- Repository built locally

## One-time setup

```bash
make dev-up
make db-migrate
make build
```

## Execute demo

```bash
./scripts/phase3-demo.sh
```

Equivalent make target:

```bash
make phase3-demo
```

## What the demo validates

- Creates a draft package version in a repo.
- Persists artifact entry metadata for a repository-committed blob digest.
- Persists and retrieves a package-type manifest (`nuget` scaffold).
- Publishes draft version to `published` with audit write and outbox emission.
- Confirms replay-safe/idempotent publish retry behavior (no duplicate `version.published` outbox event).
- Confirms publish workflow audit actions:
  - `package.version.entries.upserted`
  - `package.version.manifest.upserted`
  - `package.version.published`

## Environment variables

Optional overrides:

- `API_URL` (default: `http://127.0.0.1:8086`)
- `CONNECTION_STRING` (default local Postgres connection)
- `BOOTSTRAP_TOKEN` (default: `phase3-demo-bootstrap`)
- `POSTGRES_USER` (default: `artifortress`)
- `POSTGRES_DB` (default: `artifortress`)

Example:

```bash
BOOTSTRAP_TOKEN=my-bootstrap-token API_URL=http://127.0.0.1:8087 ./scripts/phase3-demo.sh
```

## Troubleshooting

- If API does not become healthy:
  - inspect `/tmp/artifortress-phase3-demo-api.log`.
- If SQL helper calls fail:
  - verify Postgres is running (`make dev-up`).
  - verify migrations are current (`make db-migrate`).
- If auth calls fail:
  - verify `BOOTSTRAP_TOKEN` passed to script matches API bootstrap token.
