# Performance Soak And Capacity Guide

This tranche closes:
- `ER-7-02` soak, concurrency, and noisy-neighbor test tracks
- `ER-7-03` capacity-planning guide and supported scale profiles

## Soak Evidence

The soak evidence pack is:
- `docs/reports/search-soak-drill-latest.md`
  - sustained large-index rebuild and backfill behavior
- `docs/reports/performance-soak-latest.md`
  - mixed-tenant publish/search/quarantine workload stability

The mixed-tenant soak validates:
- tenant-isolated search results under shared repo keys
- continued search progress while another tenant carries a larger backlog
- zero failed or stale-processing search jobs at steady-state completion
- stable control-plane behavior under repeated publish, rebuild, search, and quarantine activity

## Reference Environment

Current reference evidence in this repository is produced on a single-node local
environment using:
- one API process
- one worker process
- local Postgres
- local object storage

This is not an enterprise deployment topology. It is the calibration point used for
repeatable measurements and regression detection.

## Supported Initial Scale Profiles

The current supported profiles are intentionally conservative and derived from the
reference baseline plus the soak coverage above.

### Small

Use when:
- a team is validating Kublai or running a low-concurrency internal deployment
- workload shape is close to the local reference baseline

Recommended starting shape:
- `1` API replica
- `1` worker replica
- Postgres and object storage on a single availability zone with backups

Operational signals:
- upload/download throughput remains near baseline
- search backlog drains within one worker lease window after publish bursts
- no sustained failed-search growth

### Medium

Use when:
- multiple teams or multiple active repos share the deployment
- publish and search workloads overlap during business hours

Recommended starting shape:
- `2` API replicas
- `2` worker replicas
- managed Postgres with headroom for connection and write bursts
- object storage sized for sustained multipart upload concurrency

Operational signals:
- search backlog remains bounded during rebuilds
- `/v1/admin/ops/summary` stays within known baseline error and backlog thresholds
- mixed-tenant soak signals remain stable under repeated runs

### Large

Use when:
- tenants perform regular rebuilds, quarantine-heavy workflows, and concurrent publish bursts
- the deployment is treated as a shared production platform

Recommended starting shape:
- `3+` API replicas
- `3+` worker replicas
- HA Postgres topology
- durable object storage with explicit capacity monitoring

Operational signals:
- queue age, failed-search counts, and p95 request latency drive scaling decisions
- worker count is increased before API count when backlogs rise faster than request latency
- API count is increased before worker count when request latency rises under steady queue depth

## Scaling Guidance

Scale decisions should use measured evidence in this order:
1. rerun `phase2-load` and `performance-workflow-baseline`
2. rerun `performance-soak-drill`
3. compare throughput, elapsed time, backlog age, and failure counts against prior reports
4. scale the bottlenecked component rather than adding uniform replicas blindly

Interpretation:
- falling upload/download throughput points to API, object storage, or network limits
- rising pending or stale search jobs points to worker or Postgres pressure
- stable throughput with rising tenant-specific lag indicates noisy-neighbor pressure and justifies more worker capacity or tighter tenant limits

## Reproduce

```bash
make build
make test-integration
make search-soak-drill
make performance-workflow-baseline
make performance-soak-drill
```

## Acceptance

`ER-7-02` and `ER-7-03` are satisfied in this repository because:
- the soak and noisy-neighbor tracks are executable and repeatable
- capacity guidance references measured baseline and soak evidence
- supported deployment profiles are explicit enough to guide initial production sizing
