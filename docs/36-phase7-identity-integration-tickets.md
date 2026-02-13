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
| P7-03 | OIDC JWKS/RS256 validation and key-rotation support | P7-01 | todo |
| P7-04 | Group/claim-to-repository-role mapping policy | P7-02, P7-03 | todo |
| P7-05 | SAML federation contract + ACS/metadata scaffolding | P7-01 | in_progress |
| P7-06 | SAML assertion validation and role-mapping path | P7-05 | todo |
| P7-07 | Operational docs, rollout gates, and fallback controls | P7-02 through P7-06 | todo |

## Current Implementation Notes (2026-02-12)

- P7-01 completed:
  - Added config-gated OIDC bearer-token validation in `src/Artifortress.Api/Program.fs`:
    - compact JWT parsing.
    - HS256 signature validation.
    - `iss`/`aud`/`exp`/`nbf` validation.
    - repository-scope claim extraction (`scope`, `scp`, `artifortress_scopes`).
  - Added auth-source tagging (`pat`/`oidc`) on resolved principals.
  - Added `whoami` response field:
    - `authSource`.
  - Added startup configuration for:
    - `Auth:Oidc:*`
    - `Auth:Saml:*` (scaffolding and explicit not-yet-supported guard when enabled).
  - Added OIDC validation unit tests in `tests/Artifortress.Domain.Tests/Tests.fs`:
    - valid token path,
    - issuer mismatch rejection,
    - expiry rejection.
- P7-02 completed:
  - Added OIDC integration tests in `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`:
    - `P7-02 oidc bearer flow supports whoami and admin-scoped repo create`
    - `P7-02 oidc token with mismatched audience is unauthorized`
  - Added executable Phase 7 OIDC demo:
    - `scripts/phase7-demo.sh`
    - `make phase7-demo`
  - Added runbook:
    - `docs/37-phase7-runbook.md`

Latest local verification:
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "OIDC token validation"` (`3` passing tests)
- `make test` (`96` passing non-integration tests)
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "FullyQualifiedName~P7-02"` (`2` passing tests)
- `make test-integration` (`73` passing integration tests)
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` (`169` total tests)
- `make format`
