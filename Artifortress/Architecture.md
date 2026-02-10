# Artifortress Architecture Snapshot

Last updated: 2026-02-10

This document describes current architecture and explicitly separates implemented behavior from planned design.

## Core Principle

Truth and bytes are separate:
- Truth is stored in PostgreSQL (auth/authz metadata, repo metadata, role bindings, audit).
- Bytes are planned for object storage with content addressing.

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

Current auth model:
- PAT bootstrap issuance via `X-Bootstrap-Token` (for initial/admin bootstrap cases).
- Repo-scoped RBAC with `read`, `write`, `admin`, `promote`.
- `admin` wildcard scope used for global admin operations.

## Planned Runtime (Not Yet Implemented)

Data plane:
- Direct multipart upload to object storage.
- Digest verification and dedupe on blob commit.
- Download/range path backed by object store.

Metadata/publish plane:
- Draft/publish state machine for package versions.
- Atomic publish transaction for version + artifact entries + outbox + audit.

Event/index/policy plane:
- Outbox workers and external side effects.
- Search indexing pipeline.
- Quarantine/policy gates and promotion flow.

## Invariants We Already Enforce

- Token secrets are not stored in plaintext (hash-only storage).
- Revoked/expired tokens cannot authenticate.
- RBAC checks gate protected repository and binding APIs.
- Privileged actions are audited in PostgreSQL.

## Invariants Targeted Next

- Blob immutability by digest and size.
- Atomic publish semantics with no partial version visibility.
- Tombstone-first deletion with GC safety checks.
- Side effects driven exclusively from committed outbox events.

## References

- `docs/01-system-architecture.md`
- `docs/03-postgres-schema.md`
- `docs/04-state-machines.md`
- `docs/10-current-state.md`
