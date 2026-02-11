# Deployment Configuration Reference

Last updated: 2026-02-11

This reference lists runtime configuration for API, worker, and deployment scripts.

Template files:
- `deploy/staging-api.env.example`
- `deploy/staging-worker.env.example`
- `deploy/production-api.env.example`
- `deploy/production-worker.env.example`

## 1. API Configuration

| Key | Default | Required in Prod | Notes |
|---|---|---|---|
| `ConnectionStrings__Postgres` | `Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress` | yes | Primary DB connection string. |
| `Auth__BootstrapToken` | none | yes | Required to mint PATs via bootstrap path. |
| `ObjectStorage__Endpoint` | `http://localhost:9000` | yes | S3-compatible endpoint URL. |
| `ObjectStorage__AccessKey` | `artifortress` | yes | Object-store access key. |
| `ObjectStorage__SecretKey` | `artifortress` | yes | Object-store secret key. |
| `ObjectStorage__Bucket` | `artifortress-dev` | yes | Bucket for blob storage. |
| `ObjectStorage__PresignPartTtlSeconds` | `900` | recommended | Must be 60..3600; out-of-range falls back to default. |
| `Policy__EvaluationTimeoutMs` | `250` | recommended | Positive integer. |
| `Lifecycle__DefaultTombstoneRetentionDays` | `30` | recommended | Valid range: 1..3650. |
| `Lifecycle__DefaultGcRetentionGraceHours` | `24` | recommended | Valid range: 0..8760. |
| `Lifecycle__DefaultGcBatchSize` | `200` | recommended | Valid range: 1..5000. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | yes | Environment label for runtime behavior/logging. |
| `ASPNETCORE_URLS` | framework default | yes | HTTP bind address. |

## 2. Worker Configuration

| Key | Default | Required in Prod | Notes |
|---|---|---|---|
| `ConnectionStrings__Postgres` | `Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress` | yes | Worker DB connection string. |
| `Worker__PollSeconds` | `30` | recommended | Sweep interval. |
| `Worker__BatchSize` | `100` | recommended | Rows claimed per sweep. |
| `Worker__SearchJobMaxAttempts` | `5` | recommended | Retry ceiling for search jobs. |

## 3. Script/Operational Configuration

| Key | Default | Used By | Notes |
|---|---|---|---|
| `POSTGRES_USER` | `artifortress` | DB scripts | Postgres user for admin scripts. |
| `POSTGRES_DB` | `artifortress` | DB scripts | Default DB target. |
| `BACKUP_PATH` | generated under `/tmp` | `scripts/db-backup.sh`, `scripts/phase6-drill.sh` | Output SQL path. |
| `RESTORE_PATH` | none | `scripts/db-restore.sh` | Required restore input path. |
| `TARGET_DB` | `POSTGRES_DB` | `scripts/db-restore.sh` | Must match `^[A-Za-z_][A-Za-z0-9_]*$`. |
| `DRILL_DB` | `artifortress_drill` | `scripts/phase6-drill.sh` | Drill restore DB name. |
| `RTO_TARGET_SECONDS` | `900` | `scripts/phase6-drill.sh` | RTO target for drill report. |
| `RPO_TARGET_SECONDS` | `300` | `scripts/phase6-drill.sh` | RPO target for drill report. |
| `API_URL` | `http://127.0.0.1:8086` | `scripts/phase6-demo.sh` | API endpoint for demo checks. |
| `CONNECTION_STRING` | local default | `scripts/phase6-demo.sh` | API DB connection for demo run. |
| `BOOTSTRAP_TOKEN` | `phase6-demo-bootstrap` | `scripts/phase6-demo.sh` | Bootstrap token for demo PAT issue. |
| `REPORT_PATH` | script-specific report path | `phase6-*` scripts | Markdown report output location. |

## 4. Secret Handling Guidance

- Never commit real secrets to git.
- Inject production secrets from a secret manager or runtime environment.
- Rotate `Auth__BootstrapToken` after onboarding and at regular intervals.
- Use per-environment object-storage credentials and buckets.

## 5. Config Validation Checklist

Before startup:
1. Postgres connection string resolves and connects.
2. Object-storage endpoint/bucket credentials are valid.
3. Bootstrap token is non-empty for bootstrapping workflows.
4. Lifecycle and timeout values are within accepted ranges.
