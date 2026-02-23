# Phase 7 Identity Integration Tickets

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| P7-01 | OIDC bearer-token validation foundation | Phase 6 baseline | done |
| P7-02 | OIDC integration tests and runbook flow | P7-01 | done |
| P7-03 | OIDC JWKS/RS256 validation and key-rotation support | P7-01 | done |
| P7-04 | Group/claim-to-repository-role mapping policy | P7-02, P7-03 | done |
| P7-05 | SAML federation contract + ACS/metadata scaffolding | P7-01 | done |
| P7-06 | SAML assertion validation and role-mapping path | P7-05 | done |
| P7-07 | Operational docs, rollout gates, and fallback controls | P7-02 through P7-06 | done |
| P7-08 | OIDC remote JWKS refresh and key-rotation automation | P7-03, P7-07 | done |

## Completed Implementation Notes (2026-02-13)

- P7-01 and P7-02:
  - OIDC HS256 foundation, auth-source tagging, `/v1/auth/whoami`, and initial integration/demo coverage.
- P7-03:
  - Added OIDC JWKS parser and RS256 signature validation path.
  - Added `kid`-based key selection with multi-key rotation behavior.
  - OIDC runtime supports mixed signing modes:
    - HS256 via `Auth:Oidc:Hs256SharedSecret`
    - RS256 via `Auth:Oidc:JwksJson`
- P7-04:
  - Added configurable claim-to-role mappings for OIDC:
    - `Auth:Oidc:RoleMappings` with `claimName|claimValue|repoKey|role` entries.
  - JWT claims and mapped scopes are merged and deduplicated.
  - Scope resolution now accepts:
    - explicit scope claims (`scope`, `scp`, `artifortress_scopes`)
    - mapped role grants (for example `groups|af-admins|*|admin`)
- P7-05:
  - Added SAML metadata endpoint:
    - `GET /v1/auth/saml/metadata`
  - Added ACS request parsing and payload validation:
    - `POST /v1/auth/saml/acs`
    - form-urlencoded and JSON request support
    - deterministic bad-request path for invalid base64/base64url payloads
- P7-06:
  - Added SAML assertion XML parsing and validation:
    - issuer validation (`Auth:Saml:ExpectedIssuer`)
    - audience validation (`Auth:Saml:ServiceProviderEntityId`)
    - subject extraction (`NameID`)
  - Added SAML role-mapping policy:
    - `Auth:Saml:RoleMappings` using same mapping format as OIDC.
  - Added ACS exchange token issuance:
    - creates scoped PAT token on successful assertion validation
    - writes audit action `auth.saml.exchange`
- P7-07:
  - Expanded deployment/config/operations docs with OIDC RS256 + claim mapping + SAML controls.
  - Expanded `scripts/phase7-demo.sh` to validate:
    - OIDC direct scopes,
    - OIDC claim-role mapping,
    - SAML metadata and ACS exchange flow,
    - PAT compatibility fallback.
- P7-08:
  - Added optional remote JWKS endpoint integration:
    - `Auth:Oidc:JwksUrl`
    - `Auth:Oidc:JwksRefreshIntervalSeconds`
    - `Auth:Oidc:JwksRefreshTimeoutSeconds`
  - Added API-side JWKS runtime cache and refresh automation:
    - startup remote fetch bootstrap,
    - periodic background refresh timer,
    - unknown-`kid` forced-refresh retry path during token validation.
  - Added unit coverage for JWKS key merging and refresh success/failure behavior.

## Latest Local Verification

- `make format`
- `make test` (`109` passing non-integration tests)
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category=Integration" -v minimal` (`81` passing integration tests)
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` (`195` total passing tests)
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "FullyQualifiedName~P7-0" -v minimal` (`11` passing Phase 7 tests)
