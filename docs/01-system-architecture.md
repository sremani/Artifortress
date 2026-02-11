# Artifortress System Architecture

Last updated: 2026-02-11

## 1. Objectives

- Preserve integrity by construction (immutability + transactional metadata).
- Maintain low-latency reads and high-throughput uploads.
- Keep operations predictable and repairable.
- Enforce least privilege, auditability, and provenance.

## 2. Delivery State

Implemented:
- Phase 0 foundation (solution, local stack, migrations, CI checks).
- Phase 1 control-plane auth/repo APIs with PostgreSQL persistence.
- Phase 2 data plane completion (upload lifecycle, verification, dedupe, range download, throughput baseline).
- Phase 3 publish workflow completion (draft/entries/manifest/publish, outbox emission, immutability enforcement).
- Phase 4 policy/search/quarantine controls.
- Phase 5 deletion lifecycle (tombstone, GC, reconcile).
- Phase 6 hardening and GA readiness (dependency-backed readiness, ops summary, DR drill, security + rollback runbooks).

Planned later:
- OIDC/SAML identity integration.
- Search read-model query serving and rebuild/recovery maturity.

## 3. Core Architecture

Truth and bytes are intentionally separated:
- Control-plane truth in PostgreSQL.
- Blob bytes in object storage.
- Cache and index layers are rebuildable derivatives.

This separation is implemented across control-plane metadata, publish/policy workflows, and lifecycle operations.

## 4. Runtime Planes

1. Control plane (implemented)
- PAT issue/revoke and bearer auth validation.
- Repository CRUD with repo-scoped RBAC checks.
- Role binding management.
- Privileged action audit persistence.

2. Data plane (implemented)
- Upload session create + multipart lifecycle (`parts`, `complete`, `abort`).
- Commit verification for expected digest and expected size.
- Dedupe-by-digest fast path.
- Blob download with single-range support.
- Tombstone, GC, and reconcile lifecycle controls.

3. Metadata/publish plane (implemented)
- Draft version create endpoint.
- Artifact entry and manifest persistence.
- Atomic publish + outbox emission + immutability guardrails.

4. Index plane (partially implemented)
- Worker-based search-index job enqueue and processing.
- Rebuildable from PostgreSQL + object metadata.
- Query-serving read model remains planned.

5. Event plane (implemented baseline)
- Transactional outbox in PostgreSQL.
- Worker consumers for search-index enqueue/processing.
- Additional downstream consumers remain planned.

## 5. Hard Invariants

Invariants enforced now:
1. Token secret is never persisted in plaintext (hash-only).
2. Revoked and expired tokens cannot authenticate.
3. Privileged mutations are RBAC-gated and audited.
4. Published package-version identity fields are immutable at the database trigger layer.
5. Tombstone-first delete and dry-run-first GC semantics.
6. Dependency-backed readiness checks gate production readiness status.

Invariants targeted by next phases:
1. Identity federation while preserving PAT/RBAC security invariants.
2. Search read-model consistency/rebuild guarantees under outage.
3. Expanded side-effect consumers with bounded retry and operability controls.

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

Detailed deployment docs:
- `docs/26-deployment-plan.md`
- `docs/27-deployment-architecture.md`

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
- No mutable package-version identity semantics after publish.
- No direct search writes from request path once index plane is introduced.
