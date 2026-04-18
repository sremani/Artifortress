# Performance Baseline Suite

This tranche closes `ER-7-01` performance baseline suite for upload, download, publish,
search, and quarantine-heavy flows.

## Baseline Artifacts

The measured baseline suite now consists of:
- `docs/reports/phase2-load-baseline-latest.md`
  - upload and download throughput on the local reference environment
- `docs/reports/performance-workflow-baseline-latest.md`
  - publish batch completion baseline
  - search query batch baseline
  - quarantine-heavy workflow baseline

## Workflow Coverage

The baseline tracks the critical enterprise-facing workflows:
- blob upload and download through the data plane
- draft -> entries -> manifest -> publish through the control plane
- indexed search query throughput over a populated repo
- quarantine-heavy policy workflow operations over a published package set

The workflow baselines are implemented as end-to-end integration workloads rather than
isolated unit benchmarks. The goal is to preserve architectural realism while still
keeping the measurements repeatable in repository CI and local operator runs.

## Reproduce

```bash
make build
make test-integration
make phase2-load
make performance-workflow-baseline
```

## Acceptance

`ER-7-01` is satisfied in this repository because:
- baseline reports exist for the critical upload/download/publish/search/quarantine flows
- the measurements are reproducible from repository scripts
- the baseline can be rerun after changes to detect regressions against the reference environment
