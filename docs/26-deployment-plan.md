# Deployment Plan

Last updated: 2026-02-13

## 1. Goal

Ship Artifortress to staging and production with:
- deterministic rollout and rollback paths,
- dependency-backed health checks,
- repeatable migration + recovery procedures,
- clear operator ownership.

## 2. Scope

In scope:
- API deployment (`src/Artifortress.Api`).
- Worker deployment (`src/Artifortress.Worker`).
- PostgreSQL and object storage dependency readiness.
- migration, backup, restore, and DR drill workflows.
- environment configuration and operational runbooks.

Out of scope:
- signed SAML assertion cryptographic validation hardening.
- search query-serving read model (post-GA roadmap).

## 3. Baseline Topology

- API: stateless process serving HTTP.
- Worker: stateless background sweeper.
- PostgreSQL: source of truth for metadata/control plane.
- S3-compatible object storage: authoritative blob bytes.
- Optional observability stack (OTel collector + Jaeger for non-prod).

## 4. Deployment Phases

### Phase D0: Foundations (Complete)

Deliverables:
- CI restore/build/test/format checks.
- migrations and local dependency stack.
- health/readiness endpoints.

Exit criteria:
- `make build`, `make test`, and `make format` pass in CI.
- `/health/live` and `/health/ready` return expected status in local env.

### Phase D1: Staging Baseline

Deliverables:
- staging host(s) provisioned with Docker and .NET runtime.
- environment-scoped API/worker configs.
- staging DB and bucket bootstrap complete.

Exit criteria:
- staging deploy path is fully scripted/runbooked and repeatable.
- smoke checks pass after fresh deploy.

### Phase D2: Staging Soak

Deliverables:
- 7+ day staging runtime without unresolved P0/P1 defects.
- repeated migration + restart + rollback rehearsals.

Exit criteria:
- no unresolved critical stability issues.
- runbooks validated by at least one secondary operator.

### Phase D3: Production Readiness Gate

Deliverables:
- approved change window.
- backup snapshot created pre-release.
- rollback owner and communication plan assigned.

Exit criteria:
- preflight checklist complete.
- security and operational sign-off complete.

### Phase D4: Production Rollout

Deliverables:
- deploy API + worker binaries.
- run migrations once.
- validate readiness and ops summary indicators.

Exit criteria:
- readiness remains stable.
- no critical backlog growth in `/v1/admin/ops/summary`.

### Phase D5: Post-Deploy Monitoring

Deliverables:
- intensified watch window (first 24h).
- report of latency/error/backlog posture.

Exit criteria:
- no sustained `not_ready`.
- no unresolved critical incidents.

### Phase D6: DR and Recovery Validation (Recurring)

Deliverables:
- periodic backup/restore drill via `make phase6-drill`.
- updated drill report artifacts.

Exit criteria:
- RTO/RPO targets remain green.
- restore parity checks pass.

## 5. Ownership Matrix

- Platform/Infra owner:
  - host/runtime provisioning
  - networking/TLS and secrets delivery
- App owner:
  - release artifact creation
  - migrations and rollout/rollback execution
- On-call owner:
  - readiness/SLO monitoring
  - incident response and runbook execution

## 6. Deployment Risks and Mitigations

1. Risk: migration failure in production.
- Mitigation: backup before deploy, apply migrations in controlled window, immediate rollback path documented.

2. Risk: object storage degradation causing partial availability.
- Mitigation: dependency-backed `/health/ready` and alerting on readiness transitions.

3. Risk: worker lag causing stale async side effects.
- Mitigation: monitor outbox/job backlog via `/v1/admin/ops/summary`; scale worker process count if needed.

4. Risk: operator drift from intended process.
- Mitigation: step-by-step how-to runbooks and periodic drills.

## 7. Documentation Index

- `docs/27-deployment-architecture.md`
- `docs/28-deployment-howto-staging.md`
- `docs/29-deployment-howto-production.md`
- `docs/30-deployment-config-reference.md`
- `docs/31-operations-howto.md`
- `docs/23-phase6-runbook.md`
- `docs/25-upgrade-rollback-runbook.md`
