# Staging Deployment How-To

Last updated: 2026-02-12

This guide deploys API + worker into a staging environment with a dedicated Postgres DB and object-storage bucket.

## 1. Prerequisites

- .NET SDK 10.0.102+ available on deploy host.
- Docker + Compose available on deploy host.
- Access to staging Postgres and object storage.
- Repository checkout on deploy host.

## 2. Prepare Configuration

Copy and edit templates (do not commit real secrets):

```bash
cp deploy/staging-api.env.example deploy/staging-api.env
cp deploy/staging-worker.env.example deploy/staging-worker.env
```

Reference shape:

`deploy/staging-api.env`
```env
ASPNETCORE_ENVIRONMENT=Staging
ASPNETCORE_URLS=http://0.0.0.0:8086
ConnectionStrings__Postgres=Host=<pg-host>;Port=5432;Username=<user>;Password=<password>;Database=artifortress_staging
Auth__BootstrapToken=<secure-bootstrap-token>
Auth__Oidc__Enabled=false
Auth__Oidc__Issuer=https://idp.staging.example.com
Auth__Oidc__Audience=artifortress-api
Auth__Oidc__Hs256SharedSecret=<oidc-shared-secret>
Auth__Saml__Enabled=false
ObjectStorage__Endpoint=http://<object-store-endpoint>:9000
ObjectStorage__AccessKey=<access-key>
ObjectStorage__SecretKey=<secret-key>
ObjectStorage__Bucket=artifortress-staging
ObjectStorage__PresignPartTtlSeconds=900
Policy__EvaluationTimeoutMs=250
Lifecycle__DefaultTombstoneRetentionDays=30
Lifecycle__DefaultGcRetentionGraceHours=24
Lifecycle__DefaultGcBatchSize=200
```

`deploy/staging-worker.env`
```env
ConnectionStrings__Postgres=Host=<pg-host>;Port=5432;Username=<user>;Password=<password>;Database=artifortress_staging
Worker__PollSeconds=30
Worker__BatchSize=100
Worker__SearchJobMaxAttempts=5
```

## 3. Build Artifacts

```bash
make build
make test
make format
```

## 4. Bootstrap Dependencies

If using local Compose dependencies for staging rehearsal:

```bash
make dev-up
make wait-db
make storage-bootstrap
```

## 5. Apply Migrations

```bash
set -a
source deploy/staging-api.env
set +a
make db-migrate
```

## 6. Start Services

API:
```bash
set -a
source deploy/staging-api.env
set +a
dotnet src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll --urls "$ASPNETCORE_URLS"
```

Worker:
```bash
set -a
source deploy/staging-worker.env
set +a
dotnet src/Artifortress.Worker/bin/Debug/net10.0/Artifortress.Worker.dll
```

## 7. Post-Deploy Validation

```bash
curl -sS http://127.0.0.1:8086/health/live
curl -sS http://127.0.0.1:8086/health/ready
```

Issue admin token and check ops summary:
```bash
BOOTSTRAP_TOKEN="<secure-bootstrap-token>"
ADMIN_TOKEN="$(curl -sS -X POST http://127.0.0.1:8086/v1/auth/pats \
  -H "Content-Type: application/json" \
  -H "X-Bootstrap-Token: $BOOTSTRAP_TOKEN" \
  -d '{"Subject":"staging-admin","Scopes":["repo:*:admin"],"TtlMinutes":60}' | \
  sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"

curl -sS -H "Authorization: Bearer $ADMIN_TOKEN" http://127.0.0.1:8086/v1/admin/ops/summary
```

## 8. Smoke and Demo Validation

```bash
make phase1-demo
make phase2-demo
make phase3-demo
make phase4-demo
make phase5-demo
make phase6-demo
```

## 9. Staging Rollback

1. Stop API and worker processes.
2. Re-run previous known-good binaries/config.
3. If data rollback is required:
```bash
RESTORE_PATH=/tmp/artifortress-backup.sql make db-restore
```
4. Re-check `/health/ready` and `/v1/admin/ops/summary`.
