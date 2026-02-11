# Deployment Architecture

Last updated: 2026-02-11

## 1. Runtime Components

- `artifortress-api`:
  - serves HTTP API
  - enforces authz and state transitions
  - requires Postgres + object storage
- `artifortress-worker`:
  - sweeps outbox and search-index jobs
  - requires Postgres
- `postgres`:
  - source of truth for metadata/control-plane state
- `object storage (S3-compatible)`:
  - blob bytes and multipart flows

## 2. Environment Topology

## Development

- Docker Compose stack for Postgres/MinIO/Redis/OTel/Jaeger.
- API and worker run locally as dotnet processes.

## Staging

- Dedicated Postgres database.
- Dedicated object-storage bucket.
- 1+ API instances.
- 1+ worker instances.
- no shared data-plane resources with production.

## Production

- Isolated Postgres cluster and object-storage bucket.
- N API replicas behind load balancer.
- at least 1 worker replica; scale based on backlog.
- periodic backup + restore drill execution.

## 3. Logical Data Flow

1. Client sends request to API.
2. API validates authn/authz and writes metadata in Postgres.
3. Upload/download path interacts with object storage.
4. Publish emits outbox event in Postgres transaction.
5. Worker sweeps outbox and updates async job state.

## 4. Health Model

- Liveness:
  - `GET /health/live` (process is up).
- Readiness:
  - `GET /health/ready` (Postgres + object storage checks).
  - returns `200` only when dependencies are healthy.
  - returns `503` with dependency details when unhealthy.

## 5. Operational Signals

Primary admin indicator endpoint:
- `GET /v1/admin/ops/summary`

Key fields:
- `pendingOutboxEvents`
- `availableOutboxEvents`
- `oldestPendingOutboxAgeSeconds`
- `failedSearchJobs`
- `pendingSearchJobs`
- `incompleteGcRuns`
- `recentPolicyTimeouts24h`

Alert threshold baseline:
- `deploy/phase6-alert-thresholds.yaml`

## 6. Security Boundaries

- API secrets/config are environment-injected (no plaintext secrets in git).
- PAT tokens are hash-persisted only.
- repo-scoped RBAC for privileged operations.
- tenant-scoped audit log for privileged actions.

## 7. Scaling Guidance

- API scaling:
  - scale horizontally by adding stateless API instances.
- Worker scaling:
  - scale worker count when outbox/job backlog grows.
  - tune `Worker__BatchSize` and `Worker__PollSeconds`.
- DB:
  - monitor connection saturation and query latency before increasing app replicas.

## 8. Failure Domains

- Postgres outage:
  - most protected API calls degrade to service-unavailable.
- Object storage outage:
  - blob flows fail; readiness becomes `not_ready`.
- Worker outage:
  - core write path remains available; async backlog increases.

## 9. Required Invariants for Deployments

- migrations are applied before traffic is considered healthy.
- readiness endpoint must be green before cutover.
- backup snapshot taken before production changes.
- rollback path is rehearsed and documented.
