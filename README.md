# Artifortress

Artifortress is an artifact repository focused on correctness, integrity, and operational simplicity.

## Current Status

- Runtime baseline: `.NET SDK 10.0.102`, F# projects on `net10.0` and `LangVersion=10.0`.
- Phase 0: complete.
- Phase 1: complete.
- Phase 2: implemented through P2-08 (upload/download APIs + verification + coverage).
- Phase 3: started with P3-01/P3-02 completed (publish guardrails + draft version create API).
- Phase 4: started with P4-01/P4-02 completed (schema scaffold + policy evaluation API baseline).
- Current build-out focus: P2-09/P2-10 closeout, Phase 3 publish workflow completion, and Phase 4 quarantine/search follow-on work.

## Implemented Today

- Control plane:
  - PAT issue/revoke + bearer validation.
  - Repository CRUD (local/remote/virtual).
  - Repo role bindings.
  - Tenant-scoped audit query endpoint.
- Data plane (Phase 2):
  - Upload session create + multipart lifecycle (`parts`, `complete`, `abort`).
  - Commit-time digest/length verification with deterministic mismatch response.
  - Dedupe-by-digest fast path.
  - Blob download with range support and repository-scoped visibility enforcement.
  - Audit actions for upload lifecycle operations.
- Hardening updates:
  - Constant-time bootstrap token comparison.
  - Best-effort multipart abort on upload-session create race/failure paths.
  - Null-safe scope/role parsing guards in domain layer.
- Persistence:
  - Schema migrations in `db/migrations/0001_init.sql`, `db/migrations/0002_phase1_identity_and_rbac.sql`, `db/migrations/0003_phase2_upload_sessions.sql`, `db/migrations/0004_phase3_publish_guardrails.sql`, `db/migrations/0005_phase3_published_immutability_hardening.sql`, and `db/migrations/0006_phase4_policy_search_quarantine_scaffold.sql`.
- Test coverage:
  - Domain unit tests.
  - API integration tests across authz, upload lifecycle, commit verification, dedupe, range behavior, and audit action matrix.
  - Latest local verification: build + format pass, with integration tests gated on local Postgres/MinIO availability.

## Quick Start

Prerequisites:
- .NET SDK 10.0.102+
- Docker Desktop / Docker Engine with Compose
- GNU Make

Bootstrap and validate:
```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
make test
make format
```

Run Phase 1 demo flow:
```bash
make phase1-demo
```

## API Surface (Phase 2 Progress)

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
- `POST /v1/repos/{repoKey}/uploads`
- `POST /v1/repos/{repoKey}/packages/versions/drafts`
- `POST /v1/repos/{repoKey}/policy/evaluations`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/parts`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/complete`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/abort`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/commit`
- `GET /v1/repos/{repoKey}/blobs/{digest}`
- `GET /v1/audit`

## Repository Layout

- `src/Artifortress.Domain`: domain primitives and authorization invariants.
- `src/Artifortress.Api`: HTTP API with Postgres-backed control plane and Phase 2 upload/download data plane.
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
- `docs/11-phase2-implementation-tickets.md`: active Phase 2 ticket board and acceptance criteria.
- `docs/12-phase3-implementation-tickets.md`: active Phase 3 ticket board and acceptance criteria.
- `docs/13-phase4-implementation-tickets.md`: active Phase 4 ticket board and acceptance criteria.

## ADRs

Use `docs/adrs/0000-adr-template.md` for new architectural decisions.
