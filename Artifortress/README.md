# Artifortress

Artifortress is an artifact repository focused on correctness, integrity, and operational simplicity.

## Current Status

- Runtime baseline: `.NET SDK 10.0.102`, F# projects on `net10.0` and `LangVersion=10.0`.
- Phase 0: complete.
- Phase 1: complete.
- Current build-out focus: Phase 2 (blob ingest and download path).

## Implemented Today

- PostgreSQL-backed control-plane APIs:
  - PAT issue/revoke + bearer validation.
  - Repository CRUD (local/remote/virtual).
  - Repo role bindings.
  - Tenant-scoped audit query endpoint.
- Persistence:
  - Schema migrations in `db/migrations/0001_init.sql` and `db/migrations/0002_phase1_identity_and_rbac.sql`.
  - PAT hash-only storage, revocation tracking, role bindings, and audit log persistence.
- Test coverage:
  - Domain unit tests.
  - API integration tests for unauthorized, forbidden, expired, revoked, and authorized paths.

## Quick Start

Prerequisites:
- .NET SDK 10.0.102+
- Docker Desktop / Docker Engine with Compose
- GNU Make

Bootstrap and validate:
```bash
make dev-up
make db-migrate
make build
make test
make format
```

Run Phase 1 demo flow:
```bash
make phase1-demo
```

## API Surface (Phase 1)

- `GET /health/live`
- `GET /health/ready`
- `POST /v1/auth/pats`
- `POST /v1/auth/pats/revoke`
- `GET /v1/auth/whoami`
- `GET /v1/repos`
- `GET /v1/repos/{repoKey}`
- `POST /v1/repos`
- `DELETE /v1/repos/{repoKey}`
- `PUT /v1/repos/{repoKey}/bindings/{subject}`
- `GET /v1/repos/{repoKey}/bindings`
- `GET /v1/audit`

## Repository Layout

- `src/Artifortress.Domain`: domain primitives and authorization invariants.
- `src/Artifortress.Api`: HTTP API with Postgres-backed Phase 1 control plane.
- `src/Artifortress.Worker`: worker scaffold for future outbox/event processing.
- `tests/Artifortress.Domain.Tests`: unit + integration tests.
- `db/migrations`: SQL schema migrations.
- `scripts`: migration, smoke, and demo scripts.
- `.github/workflows/ci.yml`: restore/build/test/format CI workflow.

## Documentation Map

- `Architecture.md`: top-level architecture snapshot (implemented vs planned).
- `docs/01-system-architecture.md`: detailed architecture and invariants.
- `docs/05-implementation-plan.md`: phased roadmap with status.
- `docs/07-delivery-walkthrough-plan.md`: execution and demo sequencing.
- `docs/08-phase1-implementation-tickets.md`: completed Phase 1 ticket log.
- `docs/09-phase1-runbook.md`: executable Phase 1 demonstration runbook.
- `docs/10-current-state.md`: implementation inventory and known gaps.

## ADRs

Use `docs/adrs/0000-adr-template.md` for new architectural decisions.
