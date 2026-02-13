# Production Deployment How-To

Last updated: 2026-02-13

This runbook covers production rollout for API + worker with strict preflight and rollback gates.

## 1. Preflight Checklist

- CI green on target commit.
- release window approved.
- on-call + rollback owner assigned.
- production backup created:
```bash
make db-backup
```
- current ops snapshot captured (`/v1/admin/ops/summary`).

## 2. Release Artifact Preparation

```bash
make build
make test
make format
```

Publish application artifacts:
```bash
dotnet publish src/Artifortress.Api/Artifortress.Api.fsproj -c Release -o /tmp/artifortress-api-release
dotnet publish src/Artifortress.Worker/Artifortress.Worker.fsproj -c Release -o /tmp/artifortress-worker-release
```

## 3. Production Config Baseline

Copy and edit templates (do not commit real secrets):

```bash
cp deploy/production-api.env.example deploy/production-api.env
cp deploy/production-worker.env.example deploy/production-worker.env
```

API required values:
- `ConnectionStrings__Postgres`
- `Auth__BootstrapToken`
- `Auth__Oidc__Enabled` (if OIDC federation is enabled for this environment)
- `Auth__Oidc__Issuer` (when OIDC enabled)
- `Auth__Oidc__Audience` (when OIDC enabled)
- one or both OIDC signing mode inputs (when OIDC enabled):
  - `Auth__Oidc__Hs256SharedSecret`
  - `Auth__Oidc__JwksJson`
- `Auth__Oidc__RoleMappings` (recommended for claim-to-role policy)
- SAML values (when SAML enabled):
  - `Auth__Saml__IdpMetadataUrl`
  - `Auth__Saml__ExpectedIssuer`
  - `Auth__Saml__ServiceProviderEntityId`
  - `Auth__Saml__RoleMappings`
  - `Auth__Saml__IssuedPatTtlMinutes`
- `ObjectStorage__Endpoint`
- `ObjectStorage__AccessKey`
- `ObjectStorage__SecretKey`
- `ObjectStorage__Bucket`

Worker required values:
- `ConnectionStrings__Postgres`
- optional worker tuning values (`Worker__PollSeconds`, `Worker__BatchSize`, `Worker__SearchJobMaxAttempts`)

## 4. Deployment Sequence

1. Put system in deployment mode (pause non-essential cron/tasks).
2. Deploy new API and worker artifacts to hosts.
3. Apply DB migrations once:
```bash
make db-migrate
```
4. Start API instances.
5. Verify health:
```bash
curl -sS http://<api-host>/health/live
curl -sS http://<api-host>/health/ready
```
6. Start/verify worker instances.
7. Validate privileged admin signal endpoint:
```bash
curl -sS -H "Authorization: Bearer <admin-token>" http://<api-host>/v1/admin/ops/summary
```
8. Run minimal smoke flow (auth, repo read, draft/publish happy path).

## 5. Go/No-Go Gates

Go only if:
- `/health/ready` is stable and returns `status=ready`.
- error rates are within baseline.
- no critical regression in ops summary counters.

No-go and rollback if:
- sustained readiness failures.
- migration or correctness regressions.
- unresolved P0 incident during release window.

## 6. Rollback Procedure

1. Stop new API + worker release.
2. Restore previous known-good artifacts.
3. If data rollback is needed:
```bash
RESTORE_PATH=/tmp/artifortress-backup.sql make db-restore
```
4. Re-run post-rollback validation:
```bash
curl -sS http://<api-host>/health/live
curl -sS http://<api-host>/health/ready
curl -sS -H "Authorization: Bearer <admin-token>" http://<api-host>/v1/admin/ops/summary
```
5. Open incident follow-up with root-cause tracking.

## 7. Post-Deploy Monitoring Window

Recommended enhanced watch:
- first 60 minutes: active monitoring every 5 minutes.
- first 24 hours: hourly checks of readiness + ops summary.

Mandatory checks:
- readiness health transitions
- outbox/job backlog growth
- failed search job spikes
- policy timeout spikes

## 8. Related Runbooks

- `docs/23-phase6-runbook.md`
- `docs/25-upgrade-rollback-runbook.md`
- `docs/31-operations-howto.md`
