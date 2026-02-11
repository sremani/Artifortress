# Artifortress Implementation Plan

This roadmap converts the design into execution phases with exit criteria.

## Current Status Snapshot (2026-02-11)

| Phase | Status | Notes |
|---|---|---|
| Phase 0 | complete | Local stack, migrations, CI checks, smoke flow in place. |
| Phase 1 | complete | PAT lifecycle, repo CRUD, RBAC, role bindings, audit persistence, integration tests. |
| Phase 2 | complete | P2-01 through P2-10 complete, including throughput baseline/load report and executable demo/runbook. |
| Phase 3 | complete | P3-01 through P3-10 complete, including atomic publish + outbox + demo/runbook. |
| Phase 4 | complete | P4-01 through P4-10 complete, including policy/quarantine/search workflow and demo/runbook. |
| Phase 5+ | planned | GC/repair, hardening. |

## Phase 0: Foundation (1-2 weeks)

Deliverables:
- Monorepo/service skeleton and CI pipeline.
- Local dev stack (Postgres, object store emulator, worker queue).
- Baseline coding standards, migration workflow, observability bootstrap.

Exit criteria:
- `make dev-up` boots the stack reliably.
- schema migrations apply cleanly from zero.
- smoke test proves API can reach DB and object store.

## Phase 1: Identity + Repository Control Plane (2-3 weeks)

Deliverables:
- PAT/token issuance and bearer validation.
- Repo CRUD (local/remote/virtual), role bindings, scoped authorization.
- Audit logging for admin actions.

Exit criteria:
- token scopes enforced on all repo APIs.
- unauthorized access tests pass.
- audit records written for all privileged operations.

Current implementation note:
- OIDC integration is deferred and tracked for a later phase; current auth bootstrap uses PAT issuance controls.

## Phase 2: Blob Ingest and Download Path (3-4 weeks)

Deliverables:
- upload session API and multipart direct-to-object-store flow.
- digest and size verification at commit.
- dedupe on existing digest.
- download with range request support.

Exit criteria:
- upload throughput target met in load test.
- checksum mismatch path returns deterministic error.
- dedupe path avoids duplicate object writes.

## Phase 3: Package Publish Atomicity (3-4 weeks)

Deliverables:
- package/version APIs for draft and publish.
- artifact entry persistence and manifest validation.
- single-transaction publish with outbox + audit.

Exit criteria:
- chaos test confirms no partial publish state.
- immutable published version protection enforced.
- replay-safe outbox events for `version.published`.

## Phase 4: Policy, Search, and Quarantine (3-4 weeks)

Deliverables:
- policy evaluation service hooks for publish/promotion.
- quarantine workflow and promotion gate.
- search indexer consuming outbox events.

Exit criteria:
- policy deny/quarantine coverage in integration tests.
- stale or down index does not break core package resolve.
- index rebuild from source of truth completes successfully.

## Phase 5: Deletion Lifecycle + GC + Repair (2-3 weeks)

Deliverables:
- tombstone APIs with retention windows.
- mark-and-sweep garbage collector.
- reconcile and verify tooling (`metadata -> blob` and `blob -> metadata` checks).

Exit criteria:
- GC run proven safe on seeded datasets.
- reconcile tool reports and remediates drift.
- delete-to-hard-delete lifecycle validated end-to-end.

## Phase 6: Hardening and GA Readiness (2-4 weeks)

Deliverables:
- SLO dashboards and alerting.
- backup/restore runbooks.
- security review, threat model, and penetration findings closure.
- upgrade and rollback procedures.

Exit criteria:
- on-call runbook walkthrough complete.
- RPO/RTO drill passes.
- no P0/P1 open launch blockers.

## Cross-Cutting Workstreams

- Correctness:
  - property tests for state transitions.
  - migration safety checks and backward compatibility policy.
- Performance:
  - upload/download benchmarks.
  - DB query plans and index validation.
- Security:
  - token lifecycle and revocation semantics.
  - signed provenance and attestation verification path.

## Primary Risks and Mitigations

1. Risk: GC false positives causing blob loss.
Mitigation:
- use mark-and-sweep from version roots.
- require dry-run + operator approval in early environments.

2. Risk: Publish race conditions under high concurrency.
Mitigation:
- unique constraints + transaction-level locks on package/version key.
- retry-safe publish commands with idempotency key.

3. Risk: Policy engine introduces high latency.
Mitigation:
- strict timeout budget with fail-closed behavior.
- cache deterministic policy artifacts where safe.

4. Risk: Outbox backlog growth during downstream incidents.
Mitigation:
- bounded retry with dead-letter queue.
- backlog alerts and worker autoscale policies.

## Minimum Viable Release Cut

MVR includes:
- Phases 0 through 3 fully complete.
- basic Phase 5 tombstone flow (without automated hard-delete).

This enables production trials with strong integrity guarantees before full policy/search/GC maturity.
