# Security Review Closure v2

Last updated: 2026-04-01

## Scope

This document refreshes security closure after the enterprise hardening tranche that added:

- signed SAML assertion cryptographic validation
- bounded OIDC/SAML trust refresh and stale-fallback controls
- PAT governance, inventory, and blast-radius revocation controls
- release provenance workflow with SBOM and cosign signing
- worker restart/replay recovery drill

## Threat Model Refresh

Primary enterprise threat classes:

- federation trust drift during key or metadata rotation
- secret leakage in audit, logs, or operator surfaces
- excessive PAT lifetime or uncontrolled token sprawl
- replay or restart corruption in async job processing
- unverifiable release artifact provenance

## Findings

| Finding | Severity | Status | Closure |
|---|---|---|---|
| Unsigned SAML assertions could be accepted if issuer/audience matched | Critical | closed | SAML ACS now requires signature verification against IdP signing certificates. |
| Remote JWKS / metadata drift could fail ambiguously during refresh issues | High | closed | Trust material now has refresh timeout, max staleness, and explicit degraded/fallback signaling. |
| PAT admins lacked tenant-wide revocation and usage visibility | High | closed | Added PAT policy, inventory, last-used tracking, revoke-by-subject, and revoke-all controls. |
| Release consumers lacked signed provenance and SBOM evidence | High | closed | Added GitHub release provenance workflow, CycloneDX SBOMs, and cosign blob verification script. |
| Worker crash during search processing could strand jobs in `processing` | High | closed | Added lease-based reclaim logic plus restart/replay drill coverage. |
| Secret-bearing values could leak via audit or worker error logs | High | closed | Added centralized redaction coverage for audit and worker error surfaces. |

## Blocker Summary

Open P0 blockers: `0`
Open P1 blockers: `0`

Residual risks:

- release provenance workflow should be exercised on a real signed tag before procurement review
- Node 20 deprecation warnings remain in upstream GitHub Actions used by CI; this is not currently a failing security blocker but should be cleaned up
- multi-region disaster recovery is still out of scope for the current support envelope

## Required Evidence

- `make test`
- `make test-integration`
- `docs/reports/phase6-rto-rpo-drill-latest.md`
- `docs/reports/reliability-drill-latest.md`
- release provenance workflow definition in `.github/workflows/release-provenance.yml`

## Status

Security review status: **closed for current enterprise tranche**
