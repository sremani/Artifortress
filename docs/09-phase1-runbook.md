# Phase 1 Demo Runbook

This runbook demonstrates Phase 1 completion for identity, authz enforcement, repo control APIs, role bindings, and audit persistence.

## Prerequisites

- .NET SDK 10.0.102+ (`dotnet --version`)
- Docker with Compose running
- Repository built locally

## One-time setup

```bash
make dev-up
make db-migrate
make build
```

## Execute demo

```bash
./scripts/phase1-demo.sh
```

The script starts the API, runs the Phase 1 flow, and exits non-zero on any mismatch.

Equivalent make target:

```bash
make phase1-demo
```

## What the demo validates

- Admin PAT issuance through bootstrap authorization (`200`).
- Admin repo create succeeds (`201`).
- Read-only token cannot create repo (`403`).
- Repo role binding upsert succeeds for admin (`200`).
- PAT issuance with empty scopes derives repo scopes from role bindings (`200`).
- Revoked token cannot call `whoami` (`401`).
- Audit endpoint contains privileged action events (`auth.pat.issued`, `auth.pat.revoked`, `repo.created`, `repo.binding.upserted`).

## Troubleshooting

- If API does not start, inspect `/tmp/artifortress-phase1-demo.log`.
- If PAT issue returns `503`, ensure dependencies are up and migration state is current:
  - `make dev-up`
  - `make db-migrate`
- If auth endpoints return `403` unexpectedly, verify bootstrap header/token match:
  - script default token: `phase1-demo-bootstrap`
  - override with `BOOTSTRAP_TOKEN=<value> ./scripts/phase1-demo.sh`
