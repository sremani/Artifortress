# Phase 2 Throughput Baseline Report

Generated at: 2026-04-29T02:25:22Z

## Workload

- Upload flow: create -> presign part -> upload part -> complete -> commit
- Upload iterations: 12
- Download iterations: 36
- Payload size per object: 262144 bytes
- Repository: p2-load-1777429521-2572

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
| Upload elapsed (ms) | 580 |
| Upload throughput (MiB/s) | 5.17 |
| Upload requests/sec | 20.69 |
| Upload target result | PASS |
| Download total bytes | 9437184 |
| Download elapsed (ms) | 287 |
| Download throughput (MiB/s) | 31.36 |
| Download requests/sec | 125.44 |
| Download target result | PASS |

## Reproduce

```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
make phase2-load
```
