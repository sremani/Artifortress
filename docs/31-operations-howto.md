# Operations How-To

Last updated: 2026-04-18

This guide is for day-2 operations after deployment.

## 1. Daily Health Checks

1. Check liveness:
```bash
curl -sS http://<api-host>/health/live
```

2. Check readiness:
```bash
curl -sS http://<api-host>/health/ready
```

Interpret the result using:
- `ready`: all required dependencies are healthy
- `degraded`: required dependencies are healthy, but at least one optional dependency is in fallback or warning state
- `not_ready`: at least one required dependency is down and the API should stay out of traffic
- `not_configured`: appears per dependency for optional systems that are intentionally outside the active runtime contract

3. Check backlog posture:
```bash
curl -sS -H "Authorization: Bearer <admin-token>" http://<api-host>/v1/admin/ops/summary
```

Review:
- `readiness.status`
- `readiness.dependencies[*].status`
- `trustMaterial.oidc.status`
- `trustMaterial.saml.status`

## 2. How To Rotate Bootstrap Token

1. Generate new secure token.
2. Update `Auth__BootstrapToken` in secret store/runtime.
3. Restart API instances with new token.
4. Validate PAT issuance using new header value.
5. Revoke transitional tokens if applicable.

## 3. How To Rotate OIDC Signing Material

1. Determine active signing mode:
   - HS256 (`Auth__Oidc__Hs256SharedSecret`)
   - RS256 static (`Auth__Oidc__JwksJson`)
   - RS256 remote (`Auth__Oidc__JwksUrl`)
2. Rotate key material in secret store.
3. Ensure issuer/audience values remain unchanged:
   - `Auth__Oidc__Issuer`
   - `Auth__Oidc__Audience`
4. If using remote JWKS mode:
   - publish old+new keys in IdP JWKS during overlap window.
   - ensure `Auth__Oidc__JwksRefreshIntervalSeconds` and `Auth__Oidc__JwksRefreshTimeoutSeconds` are tuned for your rollout window.
5. Roll API instances only if changing local config; remote JWKS key-only rotations can roll forward automatically without API restart.
6. Validate `/v1/auth/whoami` with newly issued OIDC tokens for each enabled signing mode.
7. If claim-role mappings are enabled, also validate mapped-scope behavior for mapped claims.
8. Keep overlap window short to avoid mixed-old/new token confusion.

## 4. How To Toggle SAML Federation Safely

1. Confirm federation config is complete:
   - `Auth__Saml__IdpMetadataUrl`
   - `Auth__Saml__ExpectedIssuer`
   - `Auth__Saml__ServiceProviderEntityId`
   - `Auth__Saml__RoleMappings`
2. Enable or disable SAML via `Auth__Saml__Enabled=true|false`.
3. Roll API instances.
4. Validate:
   - `GET /v1/auth/saml/metadata`
   - `POST /v1/auth/saml/acs`
5. If rollback is needed, set `Auth__Saml__Enabled=false` and re-roll.

## 5. How To Run Migrations Safely

1. Ensure backup exists:
```bash
make db-backup
```
2. Apply migrations:
```bash
make db-migrate
```
3. Verify schema baseline:
```bash
make db-smoke
```
4. Verify readiness endpoint and smoke APIs.

## 6. How To Handle Rising Backlog

Symptoms:
- `pendingOutboxEvents` rising,
- `oldestPendingOutboxAgeSeconds` growing,
- `pendingSearchJobs` or `failedSearchJobs` increasing.

Actions:
1. Confirm worker process is healthy.
2. Temporarily increase worker capacity or reduce `Worker__PollSeconds`.
3. Increase `Worker__BatchSize` carefully.
4. Re-check metrics after 5-15 minutes.
5. Capture incident notes if backlog does not recover.

## 7. How To Run Backup/Restore Drill

Run drill:
```bash
make phase6-drill
```

Check report:
- `docs/reports/phase6-rto-rpo-drill-latest.md`

Expected:
- parity checks pass for required tables.
- RTO/RPO status is `PASS`.

## 8. How To Execute GA Readiness Sweep

```bash
make phase6-demo
```

Outputs:
- `docs/reports/phase6-rto-rpo-drill-latest.md`
- `docs/reports/phase6-ga-readiness-latest.md`

## 9. Canonical Pre-Release Battery

Run the full enterprise verification battery with:
```bash
make verify-enterprise
```

This is the canonical pre-release command because it:
- runs shared-state drills serially
- runs isolated database drills in a parallel-safe group
- refreshes the current evidence reports for throughput, recovery, reliability, search soak, and performance soak

Generated evidence includes:
- `docs/reports/phase2-load-baseline-latest.md`
- `docs/reports/phase6-rto-rpo-drill-latest.md`
- `docs/reports/reliability-drill-latest.md`
- `docs/reports/search-soak-drill-latest.md`
- `docs/reports/performance-workflow-baseline-latest.md`
- `docs/reports/performance-soak-latest.md`
- `docs/reports/upgrade-compatibility-drill-latest.md`

## 10. Incident Quick Playbook

### API is live but not ready

1. Inspect `/health/ready` dependency details.
2. Validate Postgres connectivity.
3. Validate object-storage endpoint and credentials.
4. Resolve dependency issue, then re-check readiness.

### API is degraded but still serving

1. Inspect `/health/ready` and `/v1/admin/ops/summary`.
2. Confirm whether degradation is identity trust-material fallback (`oidc_jwks` or `saml_metadata`) or an external non-critical dependency.
3. If top-level status is `degraded` because of trust-material fallback, repair remote JWKS or SAML metadata before the staleness window expires.
4. Do not declare the environment healthy again until repeated checks return `ready`.

### Worker errors or lag

1. Check worker logs for sweep errors.
2. Validate Postgres connectivity and query latency.
3. Adjust worker polling/batch settings if backlog is growing.

### Data integrity concern

1. Freeze mutating operations if necessary.
2. Run reconcile endpoint:
```bash
curl -sS -H "Authorization: Bearer <admin-token>" http://<api-host>/v1/admin/reconcile/blobs
```
3. Run DR drill in controlled environment to validate recovery path.

## 11. Recommended Weekly Cadence

- Run `make phase6-drill` at least weekly.
- Run `make verify-enterprise` before release candidates or production cutovers.
- Review ops summary trends and alert thresholds.
- Review audit log samples for privileged operations.
- Rehearse rollback path after major release changes.

## 12. Related Documents

- `docs/26-deployment-plan.md`
- `docs/27-deployment-architecture.md`
- `docs/28-deployment-howto-staging.md`
- `docs/29-deployment-howto-production.md`
- `docs/30-deployment-config-reference.md`
- `docs/46-dependency-failure-mode-matrix.md`
- `docs/25-upgrade-rollback-runbook.md`
