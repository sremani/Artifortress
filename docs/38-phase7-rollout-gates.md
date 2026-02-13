# Phase 7 Rollout Gates and Fallback Controls

Last updated: 2026-02-13

## 1. Goal

Roll out OIDC/SAML federation safely with deterministic rollback controls and no lockout risk.

## 2. Rollout Stages

1. Stage A (observe only)
- `Auth__Oidc__Enabled=true`
- `Auth__Saml__Enabled=false`
- validate OIDC HS256/RS256 and claim-role mappings in staging

2. Stage B (limited federation)
- keep OIDC enabled
- enable SAML in staging only:
  - `Auth__Saml__Enabled=true`
- validate ACS exchange and mapped-scope token issuance

3. Stage C (production canary)
- enable SAML on a small API slice
- monitor auth error rates and `/v1/admin/ops/summary` deltas
- keep PAT bootstrap fallback available

4. Stage D (full production)
- expand SAML-enabled slice to full fleet after stable canary window

## 3. Go/No-Go Gates

Go only if all are true:
- `make test` passes on release commit.
- integration test suite passes.
- Phase 7 targeted tests pass (`P7-03` through `P7-06`).
- `/health/ready` remains stable.
- no sustained increase in `401`/`403` auth failures.
- no unresolved P0/P1 incident during rollout window.

No-go if any are true:
- token validation regressions (OIDC or SAML ACS).
- readiness instability caused by config rollout.
- unexplained principal/scope mismatches in auth paths.

## 4. Immediate Fallback Controls

Primary fallback toggles:
- disable SAML:
  - `Auth__Saml__Enabled=false`
- disable OIDC if required:
  - `Auth__Oidc__Enabled=false`

Operational fallback path:
- continue with PAT issuance and PAT bearer validation.
- keep `Auth__BootstrapToken` available for emergency admin access.

## 5. Config Integrity Checks

OIDC checks:
- `Auth__Oidc__Issuer` and `Auth__Oidc__Audience` match token claims.
- at least one signing mode configured:
  - `Auth__Oidc__Hs256SharedSecret` and/or `Auth__Oidc__JwksJson`
- `Auth__Oidc__RoleMappings` syntax validates.

SAML checks:
- `Auth__Saml__ExpectedIssuer` and `Auth__Saml__ServiceProviderEntityId` are set.
- `Auth__Saml__RoleMappings` syntax validates.
- `Auth__Saml__IssuedPatTtlMinutes` is in range `5..1440`.

## 6. Post-Rollout Verification

1. OIDC `whoami` success path.
2. OIDC claim-role mapping path.
3. SAML metadata endpoint returns expected contract.
4. SAML ACS exchange returns scoped token.
5. PAT fallback path remains functional.
