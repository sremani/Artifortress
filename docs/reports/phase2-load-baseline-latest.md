# Phase 2 Throughput Baseline Report

Generated at: 2026-02-11T09:23:42Z

## Workload

- Upload flow: create -> presign part -> upload part -> complete -> commit
- Upload iterations: 12
- Download iterations: 36
- Payload size per object: 262144 bytes
- Repository: p2-load-1770801818-31719

## Environment

- OS: Darwin 25.2.0 arm64
- CPU: Apple M1 Pro
- Memory (GiB): 16.00
- .NET SDK: 10.0.102

## Targets

- Upload throughput target: >= 4.00 MiB/s
- Download throughput target: >= 6.00 MiB/s

## Results

| Metric | Value |
|---|---|
| Upload total bytes | 3145728 |
| Upload elapsed (ms) | 2453 |
| Upload throughput (MiB/s) | 1.22 |
| Upload requests/sec | 4.89 |
| Upload target result | WARN |
| Download total bytes | 9437184 |
| Download elapsed (ms) | 1242 |
| Download throughput (MiB/s) | 7.25 |
| Download requests/sec | 28.99 |
| Download target result | PASS |

## Reproduce

```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
make phase2-load
```
