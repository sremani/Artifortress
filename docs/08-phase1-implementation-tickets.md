# Phase 1 Implementation Tickets

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| P1-01 | Identity and authorization domain model (F#) | Phase 0 scaffold | done |
| P1-02 | DB migration for users, PATs, role bindings, token revocation, audit extensions | P1-01 | done |
| P1-03 | Auth token hashing and validation service | P1-01 | done |
| P1-04 | PAT issuance endpoint with scope validation and TTL rules | P1-03 | done |
| P1-05 | PAT revocation endpoint and revoked-token checks | P1-03 | done |
| P1-06 | Repository CRUD APIs (local/remote/virtual) with input validation | P1-01 | done |
| P1-07 | Repo-scoped RBAC enforcement middleware/helpers | P1-01, P1-03 | done |
| P1-08 | Role binding APIs per repository and subject | P1-06, P1-07 | done |
| P1-09 | Audit logging for privileged actions | P1-06, P1-07 | done |
| P1-10 | Auth/AuthZ test suite (scope, expiry, revoked, unauthorized) | P1-04, P1-05, P1-07 | done |
| P1-11 | Repo API integration tests (authorized and denied paths) | P1-06, P1-07, P1-08 | done |
| P1-12 | Phase 1 demo script + runbook update | P1-04 through P1-11 | done |

## Current Implementation Notes (2026-02-10)

- Implemented in domain:
  - `RepoRole`, `RepoScope`, and `Authorization.hasRole` permission evaluation.
  - Role implication rules (`admin` implies all, `write` implies `read`).
- Implemented in API (in-memory first cut):
  - PAT issue/revoke endpoints.
  - Bearer-token auth validation with SHA-256 token hash matching.
  - Repo CRUD endpoints with scope checks.
  - Role binding endpoints and scope derivation for PAT issuance.
  - Audit event capture for privileged actions.
- Persistence refactor completed for auth tokens:
  - PAT issuance now inserts into `personal_access_tokens`.
  - Bearer auth validation now reads active token state from PostgreSQL.
  - PAT revocation now updates `personal_access_tokens.revoked_at` and writes `token_revocations`.
- Persistence refactor completed for repository CRUD:
  - `GET /v1/repos` reads repositories from `repos`.
  - `GET /v1/repos/{repoKey}` reads repository details from `repos`.
  - `POST /v1/repos` inserts tenant-scoped repo rows and handles unique conflicts.
  - `DELETE /v1/repos/{repoKey}` deletes repository rows by tenant and key.
- Persistence refactor completed for role bindings:
  - `PUT /v1/repos/{repoKey}/bindings/{subject}` now upserts into `role_bindings`.
  - `GET /v1/repos/{repoKey}/bindings` now reads binding rows from `role_bindings`.
  - PAT issuance with `scopes=[]` now derives scopes from persisted `role_bindings`.
- Persistence refactor completed for audit:
  - Privileged actions now insert audit records into `audit_log`.
  - `GET /v1/audit` now reads audit rows from PostgreSQL with tenant scoping and limit.
- Phase 1 auth and repo integration tests completed:
  - Added API integration tests for unauthorized/forbidden/expired/revoked token behavior.
  - Added API integration tests for repo CRUD and role binding authorized/denied paths.
- Phase 1 demo/runbook completed:
  - Added scripted walkthrough in `scripts/phase1-demo.sh`.
  - Added runbook in `docs/09-phase1-runbook.md`.
- Implemented in DB migration:
  - `users`, `personal_access_tokens`, `token_revocations`, and `role_bindings`.

Phase 1 closure:
- Remaining open work has moved to Phase 2 planning and implementation.

## Ticket Details

## P1-01: Identity and authorization domain model (F#)

Scope:
- Add role and scope types with deterministic parsing.
- Add permission-check helpers used by API layer.

Acceptance criteria:
- Roles and scopes are strongly typed.
- Invalid scopes are rejected with explicit errors.
- Unit tests validate role implication semantics.

## P1-02: DB migration for users, PATs, role bindings, token revocation, audit extensions

Scope:
- Add SQL migration for identity/authz schema.
- Add indexes for repo+subject lookups and token validation.

Acceptance criteria:
- Migration applies from zero.
- Tables/indexes support P1 token and role lookups.

## P1-03: Auth token hashing and validation service

Scope:
- Store only token hash.
- Validate bearer token against hash, revocation, and expiry.

Acceptance criteria:
- Plain token is never persisted.
- Expired/revoked tokens are denied.

## P1-04: PAT issuance endpoint with scope validation and TTL rules

Scope:
- Add endpoint to mint token with bounded TTL.
- Validate requested scopes.

Acceptance criteria:
- Response returns token once.
- Issuance is audited.

## P1-05: PAT revocation endpoint and revoked-token checks

Scope:
- Endpoint to revoke a PAT by token id.
- Auth layer denies revoked tokens.

Acceptance criteria:
- Revoked token cannot access protected endpoints.
- Revocation action is audited.

## P1-06: Repository CRUD APIs (local/remote/virtual) with input validation

Scope:
- Create/list/get/delete repositories.
- Validate repo type-specific fields.

Acceptance criteria:
- Invalid repo payloads fail with 4xx.
- Duplicate repo key conflicts are handled.

## P1-07: Repo-scoped RBAC enforcement middleware/helpers

Scope:
- Enforce `read/write/admin/promote` with repo-specific and wildcard scopes.
- Apply auth checks uniformly across protected APIs.

Acceptance criteria:
- Admin implies write/read.
- Unauthorized/forbidden return deterministic status codes.

## P1-08: Role binding APIs per repository and subject

Scope:
- Create/update/list bindings for subject-role assignment.
- Enforce admin permission for binding operations.

Acceptance criteria:
- Binding writes are authorized and audited.
- Bindings are returned in stable shape for clients.

## P1-09: Audit logging for privileged actions

Scope:
- Record structured audit entries for token and repo admin actions.
- Include actor, action, resource, and timestamp.

Acceptance criteria:
- All privileged actions in P1 routes emit audit rows/events.

## P1-10: Auth/AuthZ test suite (scope, expiry, revoked, unauthorized)

Scope:
- Add tests for permission helper semantics and token lifecycle edge cases.

Acceptance criteria:
- Tests cover positive and negative auth scenarios.

## P1-11: Repo API integration tests (authorized and denied paths)

Scope:
- Validate endpoint behavior for allowed/denied callers.

Acceptance criteria:
- Unauthorized and forbidden paths are covered for CRUD and role bindings.

## P1-12: Phase 1 demo script + runbook update

Scope:
- Document exact commands to demonstrate Phase 1 completion.

Acceptance criteria:
- A developer can run demo flow and observe expected authz behavior.
