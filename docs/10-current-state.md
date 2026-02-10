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
- `POST /v1/repos/{repoKey}/uploads`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/parts`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/complete`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/abort`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/commit`
- `GET /v1/repos/{repoKey}/blobs/{digest}`
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
- Atomic draft/publish package version workflow.
- Transactional outbox worker processing.
- Search indexing and policy/quarantine pipeline.
- OIDC/SAML identity provider integration.
- Throughput baseline/load report publication (`P2-09`).
- Phase 2 scripted demo and runbook (`P2-10`).

## 5. Next Delivery Focus

- Phase 2 implemented work:
  - `db/migrations/0003_phase2_upload_sessions.sql` for session persistence.
  - object storage client module in `src/Artifortress.Api/ObjectStorage.fs`.
  - upload session create API: `POST /v1/repos/{repoKey}/uploads`.
  - multipart lifecycle APIs:
    - `POST /v1/repos/{repoKey}/uploads/{uploadId}/parts`
    - `POST /v1/repos/{repoKey}/uploads/{uploadId}/complete`
    - `POST /v1/repos/{repoKey}/uploads/{uploadId}/abort`
  - commit verification API:
    - `POST /v1/repos/{repoKey}/uploads/{uploadId}/commit`
  - blob download API:
    - `GET /v1/repos/{repoKey}/blobs/{digest}` (single-range support)
  - upload audit coverage:
    - session create, part URL issuance, complete, abort, commit success, and commit verification failure.
  - expanded integration coverage:
    - expired-session rejection paths.
    - dedupe-path second-create behavior.
    - invalid range (`400`) and unsatisfiable range (`416`) responses.
    - authz rejection matrix across new upload/download endpoints.
    - audit action matrix assertions for upload lifecycle operations.
- Next implementation targets:
  - add throughput baseline/load report (`P2-09`).
  - add Phase 2 demo script and runbook updates (`P2-10`).
