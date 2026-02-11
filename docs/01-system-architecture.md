# Artifortress System Architecture

Last updated: 2026-02-10

## 1. Objectives

- Preserve integrity by construction (immutability + transactional metadata).
- Maintain low-latency reads and high-throughput uploads.
- Keep operations predictable and repairable.
- Enforce least privilege, auditability, and provenance.

## 2. Delivery State

Implemented:
- Phase 0 foundation (solution, local stack, migrations, CI checks).
- Phase 1 control-plane auth/repo APIs with PostgreSQL persistence.
- Phase 2 data plane through P2-08 (upload session lifecycle, commit verification, dedupe path, blob download with range support, and integration/audit coverage).

In progress next:
- P2-09 throughput baseline and load-test reporting.
- Phase 3 kickoff (`P3-01`/`P3-02`) complete: DB publish guardrails plus draft version create API baseline.
- Phase 4 kickoff (`P4-01`) complete: DB scaffold for policy evaluation, quarantine workflow, and search indexing jobs.

Planned later:
- Atomic publish, outbox-driven integrations, search/policy pipeline, deletion lifecycle and GC hardening.

## 3. Core Architecture

Truth and bytes are intentionally separated:
- Control-plane truth in PostgreSQL.
- Blob bytes in object storage.
- Cache and index layers are rebuildable derivatives.

This separation is implemented for control-plane metadata plus Phase 2 ingest/download flows, with publish/index/event planes still planned.

## 4. Runtime Planes

1. Control plane (implemented in Phase 1)
- PAT issue/revoke and bearer auth validation.
- Repository CRUD with repo-scoped RBAC checks.
- Role binding management.
- Privileged action audit persistence.

2. Data plane (implemented through P2-08)
- Upload session create + multipart lifecycle (`parts`, `complete`, `abort`).
- Commit verification for expected digest and expected size.
- Dedupe-by-digest fast path.
- Blob download with single-range support.
- Remaining in this phase: throughput baseline/reporting (`P2-09`) and demo/runbook completion (`P2-10`).

3. Metadata/publish plane (partially implemented)
- Draft version create endpoint is live (`POST /v1/repos/{repoKey}/packages/versions/drafts`).
- Publish transaction and artifact-entry persistence flows remain in progress.

4. Index plane (planned)
- Search indexing and metadata query acceleration.
- Rebuildable from PostgreSQL + object metadata.

5. Event plane (planned)
- Transactional outbox in PostgreSQL.
- Worker consumers for indexing, notifications, and reconciliation.

## 5. Hard Invariants

Invariants enforced now:
1. Token secret is never persisted in plaintext (hash-only).
2. Revoked and expired tokens cannot authenticate.
3. Privileged mutations are RBAC-gated and audited.
4. Published package-version identity fields are immutable at the database trigger layer.

Invariants targeted by next phases:
1. Package/version-level immutability guarantees once publish workflow is implemented.
2. Atomic package version publication.
3. Tombstone-first delete followed by safe GC.
4. All side effects occur post-commit through outbox processing.

## 6. Consistency Model

Strong consistency currently required for:
- Authorization decisions.
- Repository metadata reads/writes.
- Audit and token lifecycle state.

Eventual consistency planned for:
- Search index freshness.
- Notifications and downstream consumers.
- Background repair/verification workflows.

## 7. Failure Semantics

Current:
- PostgreSQL issues degrade most protected APIs with explicit service-unavailable responses.
- Token/authz failures return deterministic `401`/`403`.

Target:
- Search outage should not break core metadata APIs.
- CDN outage should fall back to object-store origin.
- Worker outage should only increase outbox backlog.
- Partial object-store outage should preserve metadata correctness with explicit blob-read failure behavior.

## 8. Deployment Topology (Current + Planned)

Current:
- API service process (`src/Artifortress.Api`).
- PostgreSQL + MinIO + Redis + telemetry stack through `docker-compose`.

Planned:
- Horizontally scaled API/gateway services.
- Worker pool for outbox handlers.
- S3-compatible object storage as authoritative blob backend.

## 9. Security Baseline

Current:
- PATs with bounded TTL.
- Repo-scoped RBAC (`read`, `write`, `admin`, `promote`).
- Tenant-scoped audit log for privileged operations.

Planned:
- OIDC/SAML authn integration.
- Short-lived object-store upload grants.
- Attestation/signature attachment and verification.

## 10. Architectural Guardrails

- No plaintext token persistence.
- No authorization bypass for repository mutation endpoints.
- No mutable package-version semantics once publish is implemented.
- No direct search writes from request path once index plane is introduced.
