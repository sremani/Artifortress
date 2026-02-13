# Phase 7 OIDC Runbook

This runbook demonstrates the current Phase 7 identity federation flow:
- OIDC bearer-token validation (HS256 foundation mode),
- PAT compatibility under mixed auth modes,
- deterministic validation of audience-rejection behavior.

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

Run Phase 7 OIDC demo:

```bash
make phase7-demo
```

## What `phase7-demo` validates

- `make test` and `make format` pass.
- API starts with OIDC enabled and SAML disabled.
- OIDC bearer token succeeds on `/v1/auth/whoami` with:
  - expected `subject`
  - `authSource=oidc`
- OIDC admin scope allows `POST /v1/repos`.
- OIDC token with mismatched audience is rejected (`401`).
- PAT bootstrap issuance and `/v1/auth/whoami` remain functional with:
  - `authSource=pat`
- report is generated:
  - `docs/reports/phase7-oidc-latest.md`

## Environment variables

`phase7-demo` supports:
- `API_URL` (default `http://127.0.0.1:8087`)
- `CONNECTION_STRING` (default local Postgres)
- `BOOTSTRAP_TOKEN` (default `phase7-demo-bootstrap`)
- `OIDC_ISSUER` (default `https://phase7-idp.local`)
- `OIDC_AUDIENCE` (default `artifortress-api`)
- `OIDC_SHARED_SECRET` (default `phase7-demo-oidc-secret`)
- `REPORT_PATH` (default `docs/reports/phase7-oidc-latest.md`)

## Troubleshooting

- If API does not become healthy:
  - inspect `/tmp/artifortress-phase7-demo-api.log`
- If OIDC token calls return `401` unexpectedly:
  - verify `Auth__Oidc__Issuer`, `Auth__Oidc__Audience`, and `Auth__Oidc__Hs256SharedSecret` values match token claims/signing secret
- If PAT bootstrap fails:
  - verify `Auth__BootstrapToken` sent by demo matches runtime config
