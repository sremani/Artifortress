# Artifortress Delivery Walkthrough Plan

This plan defines the execution sequence we will walk through, with every phase mapped to features, dependencies, and demo milestones.

## 0. Execution Status Snapshot (2026-02-13)

- Phase 0 is complete.
- Phase 1 is complete.
- Phase 2 is complete through `P2-10`.
- Phase 3 is complete through `P3-10`.
- Phase 4 is complete through `P4-10`.
- Phase 5 is complete through `P5-08`.
- Phase 6 is complete through `P6-10`.
- Phase 7 is complete through `P7-08`.
- Next active delivery focus is post-Phase-7 hardening (signed SAML assertion cryptographic verification) and search read-model maturity.

## 1. Full Feature Inventory

Control plane features:
- PAT/scoped token issuance and repo-scoped RBAC enforcement (implemented).
- OIDC authentication integration (implemented: HS256 + RS256/JWKS + claim-role mapping).
- SAML metadata/ACS assertion exchange with mapping policy (implemented).
- Repository management (local, remote, virtual).
- Package/version metadata lifecycle (draft, publish, tombstone).
- Audit trail and transactional outbox.

Data plane features:
- Upload sessions with resumable multipart flow.
- Direct-to-object-store upload via pre-signed URLs.
- Digest/size verification and dedupe-by-digest.
- Stateless download gateway with range support.

Index/event features:
- Outbox worker processing with idempotent handlers.
- Search index pipeline (eventual consistency).
- Notifications/webhooks on publish and policy outcomes.

Policy/security features:
- Policy evaluation at publish/promotion boundaries.
- Quarantine lane and gated promotion.
- Optional signatures/attestations attached to package versions.

Lifecycle/operations features:
- Tombstone-first deletion and retention windows.
- Mark-and-sweep GC and reconcile/verify tools.
- Metrics, traces, logs, SLO dashboards, and runbooks.

## 2. Phase-by-Phase Walkthrough

## Phase 0: Foundation

Features in phase:
- Monorepo and service skeleton.
- Local dependencies stack (Postgres, object storage, Redis, telemetry).
- Migration framework and baseline schema.
- CI and code quality checks.

Dependencies:
- .NET 10 SDK + Docker daemon available.

Walkthrough demo:
- `make dev-up`
- `make db-migrate`
- `make build`
- `make test`
- API health check endpoint passes.

Exit gate:
- A new developer can clone and run the stack end-to-end in one session.

## Phase 1: Identity and Repository Control Plane

Features in phase:
- PAT/scoped token issuance.
- Repo CRUD and role binding APIs.
- Audit events for privileged operations.

Dependencies:
- Phase 0 complete.

Walkthrough demo:
- Bootstrap/issue admin PAT.
- Create repo and assign repo-scoped roles.
- Attempt authorized and unauthorized actions and inspect audit records.

Exit gate:
- AuthZ is enforced for all repository APIs.

## Phase 2: Blob Ingest and Download Path

Features in phase:
- Upload session creation and multipart instructions.
- Commit-time digest and size verification.
- Blob dedupe.
- Download endpoint with range request support.

Dependencies:
- Phase 1 auth/token model for upload authorization.

Walkthrough demo:
- Upload artifact once, re-upload same digest and show dedupe.
- Download artifact and verify checksum.
- Execute ranged download for large object.

Exit gate:
- Upload and download path hits performance and correctness baselines.

Current implementation note:
- Phase 2 is complete through `P2-10`, including throughput baseline and runbook/demo coverage.

## Phase 3: Atomic Package Publish

Features in phase:
- Draft -> publish workflow.
- Manifest validation by ecosystem.
- Atomic transaction for version + entries + audit + outbox.
- Immutable published version behavior.

Dependencies:
- Phase 2 blob availability and integrity checks.

Walkthrough demo:
- Publish a valid version and verify immutable resolution URL.
- Trigger invalid publish and prove no partial state exists.
- Show outbox event emitted only after commit.

Exit gate:
- No partial publish states under concurrent load.

## Phase 4: Policy, Search, and Quarantine

Features in phase:
- Policy hooks on publish/promotion.
- Quarantine and promotion gates.
- Search indexing from outbox stream.

Dependencies:
- Phase 3 stable publish events.

Walkthrough demo:
- Publish artifact that enters quarantine.
- Approve/promotion path unblocks release.
- Search returns indexed metadata; service remains usable with index disabled.

Exit gate:
- Policy and search are integrated without impacting core correctness path.

## Phase 5: Deletion Lifecycle, GC, and Repair

Features in phase:
- Tombstone APIs with retention windows.
- Mark-and-sweep GC.
- Reconcile and verification jobs.

Dependencies:
- Phase 3+ metadata integrity and phase 4 event pipeline.

Walkthrough demo:
- Tombstone version and confirm logical delete semantics.
- Run GC dry-run and show candidate set.
- Run reconcile and repair drift report.

Exit gate:
- Deletion lifecycle is safe, auditable, and recoverable during retention.

## Phase 6: Hardening and GA Readiness

Features in phase:
- SLO dashboards and alert thresholds.
- Backup/restore and disaster drills.
- Security review closure and rollback plan.

Dependencies:
- Phases 0-5 complete.

Walkthrough demo:
- Run RPO/RTO drill.
- Simulate partial dependency outage and validate degradation model.
- Show no P0/P1 launch blockers.

Exit gate:
- Production launch readiness sign-off.

## 3. Milestone Reviews

We will run formal walkthrough reviews at the end of each phase:
- Review input: demo artifacts, test reports, open risks, blocked decisions.
- Review output: go/no-go for next phase, ADR updates, backlog reshaping.

## 4. Definition of Done Across All Phases

- Behavior is covered by automated tests at unit/integration level.
- Observability exists for critical paths touched in the phase.
- Security checks are present for any newly exposed API.
- Runbook and operational notes are updated.
- ADRs are updated for meaningful architectural decisions.

## 5. Recommended Execution Order

1. Keep Phase 6 readiness checks in release checklist (`make phase6-demo`).
2. Prioritize OIDC/SAML identity integration with phased rollout.
3. Implement search read-model query serving and rebuild/recovery playbook.
4. Continue reliability hardening and PBT/integration stress expansion.
