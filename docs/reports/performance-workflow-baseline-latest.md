# Performance Workflow Baseline Report

Generated at: 2026-04-28T03:19:45Z

## Summary

- overall status: PASS
- upload/download reference report: `docs/reports/phase2-load-baseline-latest.md`

## Results

| Workload | Status | Operation count | Operation label | Elapsed (ms) | Ops/sec |
|---|---|---:|---|---:|---:|
| ER-701 publish workflow baseline batch completes | PASS | 24 | publish completions | 1776 | 13.51 |
| ER-701 search query baseline batch completes | PASS | 72 | search queries | 2019 | 35.66 |
| ER-701 quarantine workflow baseline batch completes | PASS | 30 | quarantine workflow operations | 1713 | 17.51 |

## Reproduce

```bash
make build
make test-integration
make performance-workflow-baseline
```
