# Artifortress Current State

Last updated: 2026-02-10

## 1. What Is Implemented

Runtime and tooling:
- `.NET SDK 10.0.102`, `net10.0`, `LangVersion=10.0`.
- Local dependency stack via `docker-compose` and `make` targets.
- SQL migration workflow with `schema_migrations` tracking.

API control-plane features:
- PAT issuance with TTL bounds and scope parsing.
- PAT revocation and active-token validation (revoked/expired denied).
- Repo CRUD with repo-scoped authorization checks.
- Role binding upsert/list for subject-role assignment per repo.
- Audit persistence for privileged operations.

Persistence:
- Core tables from `0001_init.sql` plus identity/RBAC tables from `0002_phase1_identity_and_rbac.sql`.
- Token hash persistence only; plaintext token is response-only at issuance time.

## 2. API Endpoints (Implemented)

- `GET /`
- `GET /health/live`
- `GET /health/ready`
- `GET /v1/auth/whoami`
- `POST /v1/auth/pats`
- `POST /v1/auth/pats/revoke`
- `GET /v1/repos`
- `GET /v1/repos/{repoKey}`
- `POST /v1/repos`
- `DELETE /v1/repos/{repoKey}`
- `PUT /v1/repos/{repoKey}/bindings/{subject}`
- `GET /v1/repos/{repoKey}/bindings`
- `GET /v1/audit`

## 3. Verification Status

Automated checks currently passing:
- `make test` (domain + integration tests).
- `make format`.

Demonstration assets:
- `scripts/phase1-demo.sh`
- `docs/09-phase1-runbook.md`

## 4. Known Gaps vs Target Architecture

Not implemented yet:
- Object-store-backed blob ingest and dedupe flow.
- Download/range endpoint tied to blob storage.
- Atomic draft/publish package version workflow.
- Transactional outbox worker processing.
- Search indexing and policy/quarantine pipeline.
- OIDC/SAML identity provider integration.

## 5. Next Delivery Focus

- Start Phase 2 implementation tickets for upload sessions, blob commit verification, and download path.
- Keep docs in sync by updating this file and `docs/05-implementation-plan.md` as each ticket lands.
