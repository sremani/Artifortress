# Phase 2 Throughput Baseline

Baseline run date: 2026-02-11

## Workload profile

- Upload flow: `create -> presign part -> upload part -> complete -> commit`
- Upload iterations: `12`
- Download iterations: `36`
- Payload size: `262144` bytes/object
- Environment:
  - OS: `Darwin 25.2.0 arm64`
  - CPU: `Apple M1 Pro`
  - Memory: `16.00 GiB`
  - .NET SDK: `10.0.102`

## Targets

- Upload throughput target: `>= 4.00 MiB/s`
- Download throughput target: `>= 6.00 MiB/s`

## Measured results

| Metric | Value |
|---|---|
| Upload throughput | `1.22 MiB/s` |
| Upload request rate | `4.89 req/s` |
| Upload target result | `WARN` |
| Download throughput | `7.25 MiB/s` |
| Download request rate | `28.99 req/s` |
| Download target result | `PASS` |

## Report artifact

- Raw generated report: `docs/reports/phase2-load-baseline-latest.md`

## Reproduce

```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
make phase2-load
```
