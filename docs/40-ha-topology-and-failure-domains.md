# HA Topology and Failure-Domain Runbook

Last updated: 2026-04-01

## Purpose

Define the supported high-availability topology for Kublai and make the failure-domain assumptions explicit enough for enterprise deployment review.

## Supported Production Shape

### Control Plane

- `kublai-api`
  - minimum `3` replicas
  - spread across at least `2` availability zones
  - fronted by a regional load balancer
  - readiness-gated before receiving traffic
- `kublai-worker`
  - minimum `2` replicas
  - spread across at least `2` availability zones
  - safe to scale horizontally because outbox/job claims use `FOR UPDATE SKIP LOCKED`
  - restart-safe for search jobs because stale `processing` jobs are reclaimed after lease expiry

### Data Plane Dependencies

- `Postgres`
  - managed HA cluster with synchronous failover or equivalent durability SLA
  - backups enabled and restore rehearsed
  - primary in-region; read replicas optional but not required
- `Object storage`
  - regional S3-compatible service with server-side durability guarantees
  - dedicated bucket per environment
  - no shared production bucket with staging
- `Redis`
  - optional for local/demo stack only at current scope
  - not on the critical path for current production behavior

## Failure Domains

### Availability Zone Loss

- supported when:
  - API replicas remain healthy in another zone
  - worker replicas remain healthy in another zone
  - Postgres HA service fails over cleanly
  - object storage remains regionally available
- operator actions:
  - verify `/health/ready`
  - verify `/v1/admin/ops/summary`
  - check backlog growth and search-job failures

### API Replica Loss

- impact:
  - reduced request capacity
  - no control-plane correctness risk if at least one healthy replica remains
- response:
  - autoscale or replace failed pod
  - do not cut traffic to replicas returning `degraded` or `not_ready`

### Worker Replica Loss

- impact:
  - async backlog growth only
  - publish path remains available
- response:
  - restore worker capacity
  - check `pendingOutboxEvents`, `oldestPendingOutboxAgeSeconds`, `pendingSearchJobs`, `failedSearchJobs`

### Postgres Primary Loss

- impact:
  - protected API paths return service-unavailable
  - readiness becomes `not_ready`
  - workers stop making forward progress
- supported response:
  - managed failover to standby
  - keep API behind readiness gate until green
  - only restore from backup if HA failover is unsuccessful

### Object Storage Outage

- impact:
  - upload/download/blob flows fail
  - readiness becomes `not_ready`
  - metadata-only reads may still work
- supported response:
  - restore storage dependency
  - avoid declaring recovery until readiness is green and smoke download passes

### Identity Provider / Trust Material Degradation

- impact:
  - readiness may become `degraded`
  - static fallback trust material may keep validation available within bounded staleness policy
- supported response:
  - rotate or repair remote JWKS / SAML metadata endpoint
  - inspect trust-material details in readiness and ops surfaces

## Unsupported Production Claims

These are explicitly not claimed as supported by this repository today:

- multi-region active/active metadata writes
- cross-region worker coordination for a single tenant control plane
- zero-data-loss rollback of destructive schema changes without restore
- object-store portability guarantees across vendors without validation

## Placement and Sizing Baseline

- API:
  - start at `3` replicas
  - scale when p95 latency or connection saturation rises under steady traffic
- Worker:
  - start at `2` replicas
  - scale based on backlog and job-failure trends
- Postgres:
  - provision for headroom before increasing API/worker replica count
- Object storage:
  - keep presign TTL and bucket policy consistent across all API replicas

## Cutover Gate

Do not declare a production topology healthy until all are true:

1. `/health/ready` returns `ready`
2. `/v1/admin/ops/summary` backlog metrics are inside expected baseline
3. ingress path smoke test passes
4. backup/restore and reliability drill evidence are current

## Evidence Artifacts

- `deploy/kubernetes/production/*`
- `deploy/helm/kublai/*`
- `docs/41-migration-compatibility-policy.md`
- `docs/42-operations-dashboard-and-drill-bundle.md`
