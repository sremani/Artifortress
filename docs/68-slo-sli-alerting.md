# SLO, SLI, And Alerting Guide

Last updated: 2026-04-27

## Purpose

This guide defines the initial enterprise SLO/SLI and alert-routing model for
Kublai.

These objectives are operational targets, not contractual SLAs. Contractual
SLAs must be agreed separately.

## Service Objectives

| Objective | Target | Primary Signal |
|---|---:|---|
| API readiness | `ready` during normal operation | `/health/ready` |
| Critical dependency health | Postgres and object storage ready | `/health/ready` dependency details |
| Async processing freshness | oldest pending outbox event below `900` seconds | `/v1/admin/ops/summary` |
| Search processing health | failed search jobs below critical threshold | `/v1/admin/ops/summary` |
| Lifecycle operations | no stuck incomplete GC runs | `/v1/admin/ops/summary` |
| Backup/restore confidence | latest drill status `PASS` | `docs/reports/phase6-rto-rpo-drill-latest.md` |
| Upgrade confidence | latest upgrade drill status `PASS` | `docs/reports/upgrade-compatibility-drill-latest.md` |

## Service Level Indicators

Readiness SLI:
- source: `GET /health/ready`
- good event: top-level `status` is `ready`
- bad event: top-level `status` is `not_ready`
- warning event: top-level `status` is `degraded`

Dependency SLI:
- source: `GET /health/ready`
- required ready dependencies:
  - `postgres`
  - `object_storage`
- optional degraded dependencies:
  - `oidc_jwks`
  - `saml_metadata`

Async backlog SLI:
- source: `GET /v1/admin/ops/summary`
- metrics:
  - `pendingOutboxEvents`
  - `oldestPendingOutboxAgeSeconds`
  - `pendingSearchJobs`
  - `failedSearchJobs`

Lifecycle SLI:
- source: `GET /v1/admin/ops/summary`
- metrics:
  - `incompleteGcRuns`
  - `recentPolicyTimeouts24h`

## Alert Routing

Page immediately:

- readiness status is `not_ready`
- Postgres dependency is `not_ready`
- object storage dependency is `not_ready`
- `oldestPendingOutboxAgeSeconds > 900`
- `pendingOutboxEvents > 200`
- `failedSearchJobs > 20`
- release provenance verification fails for a promoted release
- suspected cross-tenant data exposure
- legal hold or retention control appears to fail destructively

Create urgent ticket:

- readiness status is persistently `degraded`
- OIDC JWKS or SAML metadata is in fallback/degraded state
- `pendingOutboxEvents > 50`
- `oldestPendingOutboxAgeSeconds > 300`
- `pendingSearchJobs > 100`
- `failedSearchJobs > 5`
- any `incompleteGcRuns > 0`
- any `recentPolicyTimeouts24h > 0`

Create normal ticket:

- weekly drill report is stale
- enterprise verification report is stale before a release candidate
- dashboard or alert threshold drift is detected
- capacity trend suggests scaling will be needed soon

## Alert Threshold Source

The machine-readable threshold baseline is:

- `deploy/enterprise-ops-alert-thresholds.yaml`

The baseline is intentionally conservative. Operators may tune thresholds after
collecting production evidence, but release candidate sign-off must state any
environment-specific changes.

## Incident Response Linkage

Use:

- `docs/31-operations-howto.md`
- `docs/42-operations-dashboard-and-drill-bundle.md`
- `docs/66-diagnostic-error-catalog.md`
- `docs/67-support-intake-and-escalation.md`
- `scripts/support-bundle.sh`

Support requests for `SEV-1` and `SEV-2` should include readiness output, ops
summary output, and a support bundle.

## Release Gate

Before enterprise GA or production cutover:

1. dashboard is deployed
2. alert thresholds are wired
3. on-call staff can query `/v1/admin/ops/summary`
4. `make verify-enterprise` passes
5. latest production preflight report is `PASS`
6. drill reports are current
