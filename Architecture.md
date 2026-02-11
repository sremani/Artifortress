# Artifortress Architecture Snapshot

Last updated: 2026-02-11

This document describes current architecture and explicitly separates implemented behavior from planned design.

## Core Principle

Truth and bytes are separate:
- Truth is stored in PostgreSQL (auth/authz metadata, repo metadata, role bindings, audit, upload session state).
- Bytes are stored in object storage with content addressing for the Phase 2 data plane.

## Current Runtime (Implemented)

Control-plane API (`src/Artifortress.Api`) is implemented with PostgreSQL persistence:
- PAT issuance and revocation.
- Bearer-token validation from hashed PAT records.
- Repository CRUD.
- Repo role binding upsert/list.
- Audit writes for privileged actions and audit query endpoint.

Current database migrations:
- `db/migrations/0001_init.sql`
- `db/migrations/0002_phase1_identity_and_rbac.sql`
- `db/migrations/0003_phase2_upload_sessions.sql`
- `db/migrations/0004_phase3_publish_guardrails.sql`
- `db/migrations/0005_phase3_published_immutability_hardening.sql`
- `db/migrations/0006_phase4_policy_search_quarantine_scaffold.sql`
- `db/migrations/0007_phase3_manifest_persistence.sql`
- `db/migrations/0008_phase5_tombstones_gc_reconcile.sql`

Current data-plane capabilities:
- Upload session create plus multipart lifecycle (`parts`, `complete`, `abort`).
- Commit-time digest/size verification with deterministic mismatch handling.
- Dedupe by digest when blob already exists.
- Blob download endpoint with single-range support.
- Tombstone lifecycle with retention windows.
- Admin GC run API (`dry_run` and `execute`) and reconcile summary API.
- Readiness checks with live Postgres/object-storage probes.
- Admin operations summary endpoint for backlog/risk posture.

Current auth model:
- PAT bootstrap issuance via `X-Bootstrap-Token` (for initial/admin bootstrap cases).
- Repo-scoped RBAC with `read`, `write`, `admin`, `promote`.
- `admin` wildcard scope used for global admin operations.

## Planned Runtime (Post-GA)

- OIDC/SAML identity-provider integration.
- Search read-model query serving beyond queue/job scaffolding.
- Continued reliability hardening and scale posture improvements.

## Invariants We Already Enforce

- Token secrets are not stored in plaintext (hash-only storage).
- Revoked/expired tokens cannot authenticate.
- RBAC checks gate protected repository and binding APIs.
- Privileged actions are audited in PostgreSQL.
- Upload commit validates expected digest and length before session commit.
- Dedupe path prevents duplicate blob persistence for existing digest/length content.
- Published package-version identity fields are immutable at the DB trigger layer.

## Invariants Targeted Next

- Identity federation while preserving current PAT security invariants.
- Search read-model consistency/rebuild guarantees.
- Operational scale hardening under sustained backlog pressure.

## References

- `docs/01-system-architecture.md`
- `docs/03-postgres-schema.md`
- `docs/04-state-machines.md`
- `docs/10-current-state.md`
