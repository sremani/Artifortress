# Performance Workflow Baseline Report

Generated at: 2026-04-29T02:25:47Z

## Summary

- overall status: PASS
- upload/download reference report: `docs/reports/phase2-load-baseline-latest.md`

## Results

| Workload | Status | Operation count | Operation label | Elapsed (ms) | Ops/sec |
|---|---|---:|---|---:|---:|
| ER-701 publish workflow baseline batch completes | PASS | 24 | publish completions | 1866 | 12.86 |
| ER-701 search query baseline batch completes | PASS | 72 | search queries | 1857 | 38.77 |
| ER-701 quarantine workflow baseline batch completes | PASS | 30 | quarantine workflow operations | 1715 | 17.49 |

## Reproduce

```bash
make build
make test-integration
make performance-workflow-baseline
```
