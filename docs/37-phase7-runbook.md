# Phase 7 Identity Runbook

This runbook validates the implemented Phase 7 identity federation flow:
- OIDC HS256 bearer-token validation,
- OIDC RS256/JWKS bearer-token validation (integration-tested),
- OIDC claim-to-role mapping policy,
- SAML metadata + ACS assertion exchange,
- PAT fallback compatibility path.

## Prerequisites

- .NET SDK 10.0.102+ (`dotnet --version`)
- Docker with Compose running
- Local Postgres and MinIO dependencies available
- Repository built locally

## One-time setup

```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
```

## Core command

```bash
make phase7-demo
```

## What `phase7-demo` validates

- `make test` and `make format` pass.
- API starts with OIDC and SAML enabled.
- OIDC HS256 token succeeds on `/v1/auth/whoami` (`authSource=oidc`).
- OIDC admin scope allows `POST /v1/repos`.
- OIDC invalid audience is rejected (`401`).
- OIDC group claim maps to repository admin scope via configured mapping policy.
- SAML metadata endpoint returns configured SP contract.
- SAML ACS exchange validates assertion and issues scoped bearer token.
- PAT issuance + PAT `/v1/auth/whoami` path remains functional.
- report is generated at:
  - `docs/reports/phase7-oidc-latest.md`

## Environment variables

`phase7-demo` supports:
- `API_URL` (default `http://127.0.0.1:8087`)
- `CONNECTION_STRING` (default local Postgres)
- `BOOTSTRAP_TOKEN` (default `phase7-demo-bootstrap`)
- `OIDC_ISSUER` (default `https://phase7-idp.local`)
- `OIDC_AUDIENCE` (default `artifortress-api`)
- `OIDC_SHARED_SECRET` (default `phase7-demo-oidc-secret`)
- `OIDC_JWKS_URL` (optional; if set, enables remote JWKS refresh mode)
- `OIDC_JWKS_REFRESH_INTERVAL_SECONDS` (default `300`; used with `OIDC_JWKS_URL`)
- `OIDC_JWKS_REFRESH_TIMEOUT_SECONDS` (default `10`; used with `OIDC_JWKS_URL`)
- `OIDC_ROLE_MAPPINGS` (default `groups|af-admins|*|admin`)
- `SAML_IDP_METADATA_URL` (default `https://phase7-idp.local/metadata`)
- `SAML_EXPECTED_ISSUER` (default `https://phase7-idp.local/issuer`)
- `SAML_SP_ENTITY_ID` (default `urn:artifortress:phase7:sp`)
- `SAML_ROLE_MAPPINGS` (default `groups|af-admins|*|admin`)
- `REPORT_PATH` (default `docs/reports/phase7-oidc-latest.md`)
- `SKIP_STATIC_CHECKS` (default `false`; set `true` to skip `make test`/`make format` prechecks)

## Rollout and Fallback Controls

- OIDC enable/disable:
  - `Auth__Oidc__Enabled=true|false`
- OIDC signing mode controls:
  - HS256: `Auth__Oidc__Hs256SharedSecret`
  - RS256: `Auth__Oidc__JwksJson`
  - RS256 remote refresh: `Auth__Oidc__JwksUrl`
  - remote cadence/timeout:
    - `Auth__Oidc__JwksRefreshIntervalSeconds`
    - `Auth__Oidc__JwksRefreshTimeoutSeconds`
- OIDC claim mapping:
  - `Auth__Oidc__RoleMappings`
- SAML enable/disable:
  - `Auth__Saml__Enabled=true|false`
- SAML validation anchors:
  - `Auth__Saml__ExpectedIssuer`
  - `Auth__Saml__ServiceProviderEntityId`
- SAML role mapping:
  - `Auth__Saml__RoleMappings`
- SAML exchange token TTL:
  - `Auth__Saml__IssuedPatTtlMinutes`

If identity federation needs immediate rollback:
1. Set `Auth__Saml__Enabled=false`.
2. Optionally set `Auth__Oidc__Enabled=false`.
3. Continue access via PAT bootstrap/admin path while triaging.

## Troubleshooting

- If API does not become healthy:
  - inspect `/tmp/artifortress-phase7-demo-api.log`.
- If OIDC calls return `401` unexpectedly:
  - verify issuer, audience, signing mode config, and role mappings.
- If SAML ACS returns `401`:
  - verify assertion issuer/audience values match configured expected values.
  - verify mapping entries resolve at least one repository scope.
- If SAML ACS returns `400`:
  - verify `SAMLResponse` is valid base64/base64url and XML.
