# Phase 2 Throughput Baseline Report

Generated at: 2026-04-28T03:19:17Z

## Workload

- Upload flow: create -> presign part -> upload part -> complete -> commit
- Upload iterations: 12
- Download iterations: 36
- Payload size per object: 262144 bytes
- Repository: p2-load-1777346355-16668

## Environment

- OS: Linux 6.17.0-22-generic x86_64
- CPU: AMD Ryzen 9 9950X3D 16-Core Processor
- Memory (GiB): 62.35
- .NET SDK: 10.0.107

## Targets

- Upload throughput target: >= 4.00 MiB/s
- Download throughput target: >= 6.00 MiB/s

## Results

| Metric | Value |
|---|---|
| Upload total bytes | 3145728 |
| Upload elapsed (ms) | 699 |
| Upload throughput (MiB/s) | 4.29 |
| Upload requests/sec | 17.17 |
| Upload target result | PASS |
| Download total bytes | 9437184 |
| Download elapsed (ms) | 348 |
| Download throughput (MiB/s) | 25.86 |
| Download requests/sec | 103.45 |
| Download target result | PASS |

## Reproduce

```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
make phase2-load
```
