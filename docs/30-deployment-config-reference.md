# Deployment Configuration Reference

Last updated: 2026-02-13

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
| `Auth__Oidc__Enabled` | `false` | recommended | Enables OIDC bearer-token validation path. |
| `Auth__Oidc__Issuer` | none | when OIDC enabled | Exact `iss` claim value expected from incoming OIDC JWTs. |
| `Auth__Oidc__Audience` | none | when OIDC enabled | Required `aud` value expected in OIDC JWTs. |
| `Auth__Oidc__Hs256SharedSecret` | none | optional when OIDC enabled | Enables HS256 OIDC token validation mode. |
| `Auth__Oidc__JwksJson` | none | optional when OIDC enabled | JWKS JSON for RS256 signature validation; supports multi-key rotation using `kid`. |
| `Auth__Oidc__RoleMappings` | none | recommended | Semicolon-delimited claim-role entries: `claimName|claimValue|repoKey|role`. |
| `Auth__Saml__Enabled` | `false` | recommended | Enables SAML metadata + ACS assertion exchange path. |
| `Auth__Saml__IdpMetadataUrl` | none | when SAML enabled | IdP metadata URL emitted in SP metadata contract. |
| `Auth__Saml__ExpectedIssuer` | none | when SAML enabled | Exact issuer expected in incoming SAML assertions. |
| `Auth__Saml__ServiceProviderEntityId` | none | when SAML enabled | Service provider entity id and assertion audience target. |
| `Auth__Saml__RoleMappings` | none | recommended when SAML enabled | Claim-role entries: `claimName|claimValue|repoKey|role`. |
| `Auth__Saml__IssuedPatTtlMinutes` | `60` | recommended when SAML enabled | TTL for token issued by successful ACS exchange (range 5..1440). |
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
| `OIDC_ROLE_MAPPINGS` | `groups|af-admins|*|admin` | `scripts/phase7-demo.sh` | OIDC claim-role mapping demo input. |
| `SAML_EXPECTED_ISSUER` | `https://phase7-idp.local/issuer` | `scripts/phase7-demo.sh` | Expected issuer used in SAML demo assertion. |
| `SAML_SP_ENTITY_ID` | `urn:artifortress:phase7:sp` | `scripts/phase7-demo.sh` | Service provider entity id for SAML demo audience checks. |
| `SAML_ROLE_MAPPINGS` | `groups|af-admins|*|admin` | `scripts/phase7-demo.sh` | SAML claim-role mapping demo input. |
| `REPORT_PATH` | script-specific report path | `phase6-*` and `scripts/phase7-demo.sh` | Markdown report output location. |

## 4. Secret Handling Guidance

- Never commit real secrets to git.
- Inject production secrets from a secret manager or runtime environment.
- Rotate `Auth__BootstrapToken` after onboarding and at regular intervals.
- OIDC requires at least one signing mode when enabled:
  - `Auth__Oidc__Hs256SharedSecret`
  - and/or `Auth__Oidc__JwksJson`
- Use explicit federation toggles for rollout/fallback:
  - `Auth__Oidc__Enabled`
  - `Auth__Saml__Enabled`
- Use per-environment object-storage credentials and buckets.

## 5. Config Validation Checklist

Before startup:
1. Postgres connection string resolves and connects.
2. Object-storage endpoint/bucket credentials are valid.
3. Bootstrap token is non-empty for bootstrapping workflows.
4. Lifecycle and timeout values are within accepted ranges.
5. OIDC enabled mode has valid signing config (`Hs256SharedSecret` and/or `JwksJson`).
6. SAML enabled mode has valid issuer/audience anchors and role mappings.
