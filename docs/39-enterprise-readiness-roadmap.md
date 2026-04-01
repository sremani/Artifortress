# Enterprise Readiness Roadmap

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved

## Objective

This roadmap defines the execution path to make Artifortress fully enterprise-ready.

The intent is not to broaden feature scope arbitrarily. The intent is to close the
remaining trust gaps that determine whether a large organization can adopt
Artifortress for production artifact management:
- identity and security closure
- hard multi-tenant isolation
- operational reliability and HA
- governance and compliance controls
- deployment/upgrade safety
- performance certification
- supportability and procurement readiness

This plan assumes the current repository baseline described in:
- `README.md`
- `docs/10-current-state.md`
- `docs/24-security-review-closure.md`
- `docs/25-upgrade-rollback-runbook.md`
- `docs/27-deployment-architecture.md`
- `docs/31-operations-howto.md`

## Phase Map

| Phase | Goal | Primary Outcome |
|---|---|---|
| ER-1 | Security closure | no obvious enterprise security blockers |
| ER-2 | Tenant isolation | safe shared deployment posture |
| ER-3 | Reliability and HA | well-defined failure semantics and recovery |
| ER-4 | Search maturity | trustworthy search/read-model behavior |
| ER-5 | Governance and compliance | auditable and policy-driven control plane |
| ER-6 | Deployment and operability | low-friction, deterministic production rollout |
| ER-7 | Performance and scale | evidence-backed enterprise capacity claims |
| ER-8 | Productization and support | procurement-ready enterprise package |

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| ER-1-01 | SAML signed-assertion cryptographic validation | Phase 7 baseline | done |
| ER-1-02 | OIDC/SAML trust material rotation and fail-safe controls | ER-1-01 | done |
| ER-1-03 | PAT governance, revocation blast-radius, and usage visibility | ER-1-01 | done |
| ER-1-04 | Secret redaction and sensitive-field logging audit | ER-1-01 | done |
| ER-1-05 | Signed release artifacts, SBOM, and supply-chain verification lane | ER-1-01 | in_progress |
| ER-1-06 | Security review closure v2 and threat-model refresh | ER-1-02 through ER-1-05 | todo |
| ER-2-01 | Formal tenant isolation invariants across API and worker paths | ER-1-06 | todo |
| ER-2-02 | Cross-tenant negative integration and stress coverage | ER-2-01 | todo |
| ER-2-03 | Tenant quotas and admission controls | ER-2-01 | todo |
| ER-2-04 | Delegated administration and tenant-scoped operational roles | ER-2-01 | todo |
| ER-3-01 | Background job idempotency and replay-safety hardening | ER-2-02 | todo |
| ER-3-02 | Dependency failure-mode matrix and deterministic readiness/degradation semantics | ER-3-01 | in_progress |
| ER-3-03 | Worker/API crash, restart, and duplicate-delivery drill automation | ER-3-01 | done |
| ER-3-04 | HA deployment topology and failure-domain runbook | ER-3-02, ER-3-03 | todo |
| ER-4-01 | Search relevance/ranking maturity and deterministic query semantics | ER-3-01 | todo |
| ER-4-02 | Search rebuild control plane: pause/resume/cancel/status | ER-4-01 | todo |
| ER-4-03 | Search lag, freshness, and stuck-job observability | ER-4-02 | todo |
| ER-4-04 | Large-index rebuild and backfill soak coverage | ER-4-02, ER-4-03 | todo |
| ER-5-01 | Immutable audit export and correlation model | ER-3-02 | todo |
| ER-5-02 | Repository/org policy inheritance and protected-artifact rules | ER-5-01 | todo |
| ER-5-03 | Dual-control workflows for high-risk admin actions | ER-5-01 | todo |
| ER-5-04 | Compliance evidence pack and retention/legal-hold controls | ER-5-02, ER-5-03 | todo |
| ER-6-01 | Production Helm/manifests hardening and config closure | ER-3-04 | in_progress |
| ER-6-02 | Zero-downtime or bounded-downtime migration policy | ER-6-01 | todo |
| ER-6-03 | Multi-version upgrade compatibility and rollback matrix | ER-6-02 | todo |
| ER-6-04 | Operations dashboard, alerts, and incident drill bundle | ER-6-01 | todo |
| ER-7-01 | Performance baseline suite for upload/download/publish/search | ER-6-01 | todo |
| ER-7-02 | Soak, concurrency, and noisy-neighbor test tracks | ER-7-01, ER-2-03 | todo |
| ER-7-03 | Capacity-planning guide and supported scale profiles | ER-7-01, ER-7-02 | todo |
| ER-8-01 | Enterprise deployment guide, security whitepaper, and admin handbook | ER-6-04, ER-1-06 | todo |
| ER-8-02 | Versioning, deprecation, and support-window policy | ER-8-01 | todo |
| ER-8-03 | Enterprise GA checklist and launch sign-off board | ER-7-03, ER-8-02, ER-5-04 | todo |

## Current Sequencing Guidance

Recommended execution order:
1. ER-1 security closure
2. ER-3 reliability and HA
3. ER-6 deployment and operability
4. ER-5 governance and compliance
5. ER-2 tenant isolation
6. ER-4 search maturity
7. ER-7 performance and scale
8. ER-8 enterprise GA packaging

Rationale:
- Enterprises reject products first on security, reliability, and operability.
- Tenant isolation and governance matter early, but reliability proof and deployment
  safety usually gate serious production adoption first.
- Search and scale work should be validated against the hardened operational model,
  not designed in isolation.

## Ticket Details

## ER-1 Security Closure

### ER-1-01: SAML signed-assertion cryptographic validation

Scope:
- Validate SAML assertion signatures against IdP metadata.
- Reject unsigned or invalidly signed assertions deterministically.
- Cover certificate rollover and metadata parsing edge cases.

Acceptance criteria:
- Assertion trust is based on cryptographic verification, not just issuer/audience checks.
- Invalid signatures and stale signing material fail closed.
- Integration tests cover success, invalid signature, wrong cert, expired cert, and rotated cert.

### ER-1-02: OIDC/SAML trust material rotation and fail-safe controls

Scope:
- Add runtime controls for JWKS/cert refresh intervals, cache state, and safe fallback behavior.
- Expose trust-material freshness and failure state through admin/ops surfaces.

Acceptance criteria:
- Unknown-key and stale-key scenarios are observable and bounded.
- Refresh failures degrade safely without silently broadening trust.

### ER-1-03: PAT governance, revocation blast-radius, and usage visibility

Scope:
- Add org-level PAT issuance policy and max-TTL enforcement.
- Add token last-used visibility and revoke-all / revoke-by-subject controls.

Acceptance criteria:
- Admins can constrain PAT risk surface without direct DB intervention.
- Audit trail exists for issuance, revoke, revoke-all, and policy changes.

### ER-1-04: Secret redaction and sensitive-field logging audit

Scope:
- Audit logs, traces, exception messages, and API responses for credential leakage.
- Add explicit redaction helpers and regression tests.

Acceptance criteria:
- Known secret-bearing fields never appear in logs or audit payloads in plaintext.
- Redaction invariants have automated test coverage.

### ER-1-05: Signed release artifacts, SBOM, and supply-chain verification lane

Scope:
- Produce signed binaries/images and attach SBOMs in CI.
- Add verification procedure and release evidence artifacts.

Acceptance criteria:
- Releases are reproducibly signed.
- Consumers can verify provenance using documented steps.

### ER-1-06: Security review closure v2 and threat-model refresh

Scope:
- Refresh the threat model after identity and supply-chain hardening.
- Track open findings with severity and disposition.

Acceptance criteria:
- No open P0/P1 enterprise launch blockers remain.
- Review artifacts are committed and actionable.

## ER-2 Tenant Isolation

### ER-2-01: Formal tenant isolation invariants across API and worker paths

Scope:
- Document and encode tenant-boundary rules across repository, package, blob, audit, and search paths.
- Review worker job payloads and rebuild flows for tenant leakage risk.

Acceptance criteria:
- Isolation invariants are explicit in docs and enforced in code.
- All cross-tenant access paths fail closed.

### ER-2-02: Cross-tenant negative integration and stress coverage

Scope:
- Add integration tests attempting cross-tenant read/write/search/quarantine access.
- Add stress tests for mixed-tenant workloads.

Acceptance criteria:
- Negative tests cover API and background processing paths.
- No cross-tenant visibility occurs under concurrent execution.

### ER-2-03: Tenant quotas and admission controls

Scope:
- Add tenant-level storage, request, and background-job quotas.
- Add deterministic quota errors and operational observability.

Acceptance criteria:
- Quota enforcement is configurable and audited.
- Over-limit tenants do not destabilize the shared system.

### ER-2-04: Delegated administration and tenant-scoped operational roles

Scope:
- Add tenant admin / auditor / operator roles with least-privilege boundaries.

Acceptance criteria:
- Shared-hosting customers can self-administer within tenant bounds.
- Platform-wide admin privileges are no longer required for routine tenant operations.

## ER-3 Reliability and HA

### ER-3-01: Background job idempotency and replay-safety hardening

Scope:
- Review publish, search, quarantine, GC, reconcile, and audit-adjacent job paths for replay behavior.
- Add duplicate-delivery and crash-recovery tests.

Acceptance criteria:
- Replayed jobs do not corrupt state or duplicate externally visible side effects.

### ER-3-02: Dependency failure-mode matrix and deterministic readiness/degradation semantics

Scope:
- Define exact behavior for Postgres, object storage, Redis, and telemetry degradation.
- Extend readiness and ops summary to reflect degraded states clearly.

Acceptance criteria:
- Operators can distinguish hard-down, degraded, and healthy conditions deterministically.

### ER-3-03: Worker/API crash, restart, and duplicate-delivery drill automation

Scope:
- Add drills covering mid-flight publish, interrupted rebuild, and worker restart scenarios.

Acceptance criteria:
- Drill scripts exit non-zero on replay/corruption regressions.
- Recovery expectations are documented and repeatable.

### ER-3-04: HA deployment topology and failure-domain runbook

Scope:
- Define supported HA topology for API, worker, Postgres, object storage, and Redis.
- Document what is and is not supported in enterprise production.

Acceptance criteria:
- HA guidance is specific enough to deploy without guesswork.
- Failure-domain boundaries and assumptions are explicit.

## ER-4 Search Maturity

### ER-4-01: Search relevance/ranking maturity and deterministic query semantics

Scope:
- Improve ranking and query behavior without sacrificing predictability.
- Freeze search semantics for documented query classes.

Acceptance criteria:
- Search relevance rules are documented and testable.
- Published, quarantined, and tombstoned behaviors remain deterministic.

### ER-4-02: Search rebuild control plane: pause/resume/cancel/status

Scope:
- Add operational controls for rebuild execution and visibility.

Acceptance criteria:
- Rebuilds can be safely managed during large backfills or incidents.
- Admin UI/API contract is deterministic.

### ER-4-03: Search lag, freshness, and stuck-job observability

Scope:
- Add metrics and ops summary signals for projection lag and stuck processing.

Acceptance criteria:
- Operators can detect when search is stale or backlogged.

### ER-4-04: Large-index rebuild and backfill soak coverage

Scope:
- Validate rebuild behavior under large datasets and long-running backfills.

Acceptance criteria:
- Rebuild tooling works under sustained load without silent drift or starvation.

## ER-5 Governance and Compliance

### ER-5-01: Immutable audit export and correlation model

Scope:
- Add exportable audit artifact format and correlation IDs spanning API and worker actions.

Acceptance criteria:
- Enterprises can ship audit data externally without bespoke DB access.

### ER-5-02: Repository/org policy inheritance and protected-artifact rules

Scope:
- Add policy inheritance at org/repo levels.
- Add protected-package / immutable-retention controls.

Acceptance criteria:
- Policy model can express common enterprise governance rules.

### ER-5-03: Dual-control workflows for high-risk admin actions

Scope:
- Add optional approval workflows for destructive operations such as hard GC or sensitive policy changes.

Acceptance criteria:
- High-risk actions can require more than one actor.

### ER-5-04: Compliance evidence pack and retention/legal-hold controls

Scope:
- Add evidence-generation paths and legal-hold style retention controls.

Acceptance criteria:
- Artifact lifecycle can satisfy audit/compliance scenarios without manual DB edits.

## ER-6 Deployment and Operability

### ER-6-01: Production Helm/manifests hardening and config closure

Scope:
- Finish and validate production deployment assets, defaults, and examples.

Acceptance criteria:
- A new operator can deploy staging/production from repo assets and docs alone.

### ER-6-02: Zero-downtime or bounded-downtime migration policy

Scope:
- Classify migrations and define rollout strategy by migration type.

Acceptance criteria:
- Upgrade procedure explicitly states downtime expectations and fallback path.

### ER-6-03: Multi-version upgrade compatibility and rollback matrix

Scope:
- Validate upgrade and rollback across multiple supported versions.

Acceptance criteria:
- Supported upgrade paths are explicit and tested.

### ER-6-04: Operations dashboard, alerts, and incident drill bundle

Scope:
- Provide dashboard definitions, alert thresholds, and periodic drill procedures.

Acceptance criteria:
- On-call operators have a complete baseline operational pack.

## ER-7 Performance and Scale

### ER-7-01: Performance baseline suite for upload/download/publish/search

Scope:
- Extend current throughput baseline to cover publish, search, and quarantine-heavy flows.

Acceptance criteria:
- Performance reports exist for the critical enterprise workflows.

### ER-7-02: Soak, concurrency, and noisy-neighbor test tracks

Scope:
- Add long-duration and mixed-tenant performance/stability scenarios.

Acceptance criteria:
- The system remains stable under sustained mixed workload.

### ER-7-03: Capacity-planning guide and supported scale profiles

Scope:
- Publish supported sizing profiles and scaling guidance.

Acceptance criteria:
- Capacity planning is based on measured evidence, not narrative estimates.

## ER-8 Productization and Support

### ER-8-01: Enterprise deployment guide, security whitepaper, and admin handbook

Scope:
- Consolidate enterprise-facing documentation into a procurement-friendly package.

Acceptance criteria:
- Security, deployment, and admin concerns are documented in customer-consumable form.

### ER-8-02: Versioning, deprecation, and support-window policy

Scope:
- Define compatibility and support commitments.

Acceptance criteria:
- Enterprises know how long versions are supported and how changes are introduced.

### ER-8-03: Enterprise GA checklist and launch sign-off board

Scope:
- Create final launch board tying together all evidence and remaining exceptions.

Acceptance criteria:
- Enterprise GA can be signed off with no hidden dependencies.

## Suggested Milestone Packaging

Recommended milestone groupings:

### Milestone A: Enterprise Security Baseline
- ER-1-01 through ER-1-06

### Milestone B: Shared Hosting Safety
- ER-2-01 through ER-2-04
- ER-3-01

### Milestone C: Reliable Production Operations
- ER-3-02 through ER-3-04
- ER-6-01 through ER-6-04

### Milestone D: Governance and Search Maturity
- ER-4-01 through ER-4-04
- ER-5-01 through ER-5-04

### Milestone E: Enterprise GA Evidence
- ER-7-01 through ER-7-03
- ER-8-01 through ER-8-03
