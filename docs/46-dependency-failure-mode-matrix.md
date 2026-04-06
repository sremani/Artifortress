# Dependency Failure-Mode Matrix

Last updated: 2026-04-06

## Purpose

Define the exact dependency semantics behind `/health/ready` and `/v1/admin/ops/summary`.

## Status Model

- `ready`: dependency is healthy and operating normally
- `degraded`: dependency is operating on bounded fallback or with warning conditions
- `not_ready`: dependency cannot safely satisfy its contract
- `not_configured`: dependency is intentionally not part of the active runtime contract

## Overall Readiness Rules

`/health/ready` and `/v1/admin/ops/summary.readiness.status` use the same rules:

1. if any essential dependency is `not_ready`, overall status is `not_ready`
2. else if any dependency is `degraded` or `not_ready`, overall status is `degraded`
3. else overall status is `ready`

That means optional identity dependencies can degrade the environment without forcing a hard traffic cut, while Postgres and object storage still fail closed.

## Dependency Matrix

| Dependency | Criticality | `ready` | `degraded` | `not_ready` | `not_configured` |
|---|---|---|---|---|---|
| `postgres` | essential | connection probe succeeds | never | connect/query probe fails | never |
| `object_storage` | essential | availability probe succeeds | never | availability probe fails or times out | never |
| `oidc_jwks` | optional | local signing material only, or remote JWKS refresh is fresh | remote refresh warning, or stale remote with valid static fallback keys | remote JWKS stale/no successful refresh and no valid fallback | OIDC disabled |
| `saml_metadata` | optional | static signing certificates available, or remote metadata refresh is fresh | remote refresh warning, or stale remote metadata with valid static fallback certs | no signing certs available, or metadata stale with no fallback | SAML disabled |
| `redis` | optional | not used in current production contract | never | never | Redis omitted from current production scope |
| `telemetry` | optional | collector/exporter pipeline is external and healthy by separate monitoring | exporter pipeline warning handled outside readiness | telemetry pipeline outage does not gate API readiness | telemetry health not enforced by runtime readiness |

## Operator Interpretation

- `not_ready`:
  - remove API instance from traffic
  - investigate Postgres or object-storage failure first
- `degraded`:
  - keep traffic only if required business paths still pass
  - inspect trust-material detail strings and admin ops summary
- `not_configured`:
  - expected for optional systems not in the runtime contract
  - do not treat as failure by itself

## Required Checks

1. inspect `GET /health/ready`
2. inspect `GET /v1/admin/ops/summary`
3. confirm the dependency-level `status` entries, not only the top-level status
4. do not declare recovery until dependency states are stable across repeated checks
