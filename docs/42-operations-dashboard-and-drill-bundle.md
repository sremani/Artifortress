# Operations Dashboard and Drill Bundle

Last updated: 2026-04-01

## Purpose

Provide the baseline operational pack required for on-call ownership of Artifortress in an enterprise environment.

## Included Assets

- dashboard definition:
  - `deploy/grafana-artifortress-operations-dashboard.json`
- alert thresholds:
  - `deploy/enterprise-ops-alert-thresholds.yaml`
  - `deploy/phase6-alert-thresholds.yaml`
- periodic drills:
  - `scripts/phase6-drill.sh`
  - `scripts/reliability-drill.sh`
  - `scripts/enterprise-ops-drill.sh`

## Core Dashboard Signals

### Readiness

- overall readiness state
- OIDC trust health
- SAML trust health

### Async Pipeline

- pending outbox events
- oldest pending outbox age
- pending search jobs
- failed search jobs

### Lifecycle Operations

- incomplete GC runs
- recent policy timeouts

## Alerting Baseline

Alerting should page on:

- readiness `not_ready`
- sustained outbox age beyond critical threshold
- sustained search-job failure growth
- incomplete GC runs that do not clear

Alerting should warn on:

- readiness `degraded`
- trust-material fallback operation
- rising pending search backlog
- any recent policy timeouts

## Incident Drill Cadence

- daily:
  - readiness and ops summary review for active environments
- weekly:
  - `make phase6-drill`
  - `./scripts/reliability-drill.sh`
- before production cutover:
  - `ADMIN_TOKEN=<token> ./scripts/enterprise-ops-drill.sh`

## Incident Response Flow

1. inspect `/health/ready`
2. inspect `/v1/admin/ops/summary`
3. classify impact:
   - control-plane outage
   - async degradation
   - trust-material degradation
   - data-plane outage
4. run the matching drill if recovery confidence is low
5. do not declare recovery until readiness and backlog signals stabilize

## Evidence Outputs

- `docs/reports/phase6-rto-rpo-drill-latest.md`
- `docs/reports/reliability-drill-latest.md`
- `docs/reports/enterprise-ops-drill-latest.md`

## Operator Minimum

An environment is not considered enterprise-operable unless:

- dashboard is deployed
- alert thresholds are wired
- admin token access for ops summary is available to on-call staff
- drill reports are current and reviewable
