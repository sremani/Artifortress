# Phase 2 Runbook

This runbook demonstrates Phase 2 upload/download behavior and executes the Phase 2 throughput baseline workload.

## Prerequisites

- .NET SDK 10.0.102+.
- Docker with Compose running.
- Local dependencies available (`Postgres`, `MinIO`).

## One-time setup

```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
```

## Demo flow (P2-10)

Run:

```bash
make phase2-demo
```

This validates:

- upload lifecycle (`create -> parts -> complete -> commit`)
- dedupe second-create behavior (`deduped=true`, `state=committed`)
- deterministic mismatch path (`409`, `upload_verification_failed`)
- full and ranged download correctness
- upload lifecycle audit actions

## Throughput baseline flow (P2-09)

Run:

```bash
make phase2-load
```

This executes a repeatable load shape:

- upload iterations: `12`
- download iterations: `36`
- payload size: `262144` bytes/object
- upload target: `>= 4.00 MiB/s`
- download target: `>= 6.00 MiB/s`

Generated report:

- `docs/reports/phase2-load-baseline-latest.md`

## Optional tuning via environment variables

- `UPLOAD_ITERATIONS`
- `DOWNLOAD_ITERATIONS`
- `PAYLOAD_BYTES`
- `UPLOAD_TARGET_MBPS`
- `DOWNLOAD_TARGET_MBPS`
- `ENFORCE_TARGETS` (`true|false`)
- `REPORT_PATH`
- `API_URL`
- `CONNECTION_STRING`
- `BOOTSTRAP_TOKEN`
- `DOTNET_BIN`

Example:

```bash
UPLOAD_ITERATIONS=24 DOWNLOAD_ITERATIONS=72 PAYLOAD_BYTES=524288 make phase2-load
```

## Troubleshooting

- If API startup fails in script execution:
  - verify runtime and SDK (`dotnet --version`).
  - inspect logs:
    - `/tmp/artifortress-phase2-demo.log`
    - `/tmp/artifortress-phase2-load.log`
- If uploads fail at pre-signed URL step:
  - verify MinIO service is running and bucket exists.
  - rerun `make storage-bootstrap`.
- If DB operations fail:
  - verify Postgres service is running.
  - rerun `make db-migrate`.
