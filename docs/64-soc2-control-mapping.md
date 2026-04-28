# SOC 2-Style Control Mapping

Last updated: 2026-04-27

## Purpose

This document maps Artifortress repository evidence to SOC 2-style control
themes. It is not a SOC 2 report and does not claim auditor attestation.

The goal is to make procurement and compliance review faster by identifying:

- implemented product controls
- evidence artifacts
- customer responsibilities
- current gaps that require follow-up tickets

## Control Status Key

- `implemented`: product control exists with repository evidence
- `partial`: product control exists but needs productization or external
  evidence before GA
- `customer-owned`: control depends primarily on the customer environment
- `gap`: tracked follow-up required

## Security

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Authentication | bootstrap token, PATs, OIDC, SAML | implemented | `docs/37-phase7-runbook.md`, `docs/43-security-review-closure-v2.md` |
| SAML trust | signed assertion verification | implemented | `docs/43-security-review-closure-v2.md` |
| OIDC/SAML trust rotation | bounded refresh, degraded signaling | implemented | `docs/43-security-review-closure-v2.md` |
| Token governance | PAT policy, inventory, last-used, revoke controls | implemented | `docs/43-security-review-closure-v2.md` |
| Authorization | repo-scoped RBAC and tenant roles | implemented | `docs/45-tenant-isolation-and-admission-controls.md` |
| Tenant isolation | tenant filters and negative coverage | implemented | `docs/45-tenant-isolation-and-admission-controls.md` |
| Secret redaction | audit and worker redaction coverage | implemented | `docs/43-security-review-closure-v2.md` |
| Network security | ingress, TLS, firewall policy | customer-owned | `docs/59-enterprise-product-envelope.md` |
| Vulnerability intake | disclosure and patch SLA policy | gap | `EGA-17` |

## Availability

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Readiness | dependency-backed `/health/ready` | implemented | `docs/27-deployment-architecture.md` |
| Operational health | `/v1/admin/ops/summary` | implemented | `docs/42-operations-dashboard-and-drill-bundle.md` |
| HA topology | multi-replica API/worker shape | partial | `docs/40-ha-topology-and-failure-domains.md`, `EGA-12` |
| Backup/restore | scripted drill evidence | implemented | `docs/reports/phase6-rto-rpo-drill-latest.md` |
| Upgrade compatibility | supported baseline upgrade drill | implemented | `docs/reports/upgrade-compatibility-drill-latest.md` |
| Replay safety | worker idempotency and lease reclaim drills | implemented | `docs/reports/reliability-drill-latest.md` |
| SLO/SLI policy | customer-facing objectives and routing | gap | `EGA-11` |
| Support bundle | redacted diagnostic collection | gap | `EGA-10` |

## Confidentiality

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Token storage | PAT hashes only | implemented | `docs/62-enterprise-security-whitepaper.md` |
| Secret handling | environment-injected secrets | implemented | `docs/27-deployment-architecture.md` |
| Data segregation | tenant-owned aggregates and filters | implemented | `docs/45-tenant-isolation-and-admission-controls.md` |
| Audit scoping | tenant-scoped audit query | implemented | `docs/49-compliance-evidence-and-legal-holds.md` |
| Encryption at rest | database/object-storage provider configuration | customer-owned | `docs/62-enterprise-security-whitepaper.md` |
| TLS in transit | ingress/load-balancer configuration | customer-owned | `docs/62-enterprise-security-whitepaper.md` |

## Processing Integrity

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Upload integrity | digest and length verification | implemented | `README.md`, `docs/10-current-state.md` |
| Content addressing | SHA-256 digest identity and dedupe | implemented | `README.md` |
| Publish integrity | atomic publish and outbox emission | implemented | `docs/10-current-state.md` |
| Immutable published state | DB trigger hardening | implemented | `db/migrations/0005_phase3_published_immutability_hardening.sql` |
| Search integrity | rebuild/backfill soak coverage | implemented | `docs/reports/search-soak-drill-latest.md` |
| Legal hold integrity | GC respects held artifacts | implemented | `docs/49-compliance-evidence-and-legal-holds.md` |

## Privacy

Artifortress is an artifact repository and does not currently claim to be a
privacy-management system.

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Data minimization | product stores artifact metadata and audit data needed for operation | partial | `docs/63-procurement-evidence-pack.md` |
| Privacy request workflow | data subject request workflow | gap | future product decision |
| Customer content classification | artifact content classification | customer-owned | `docs/59-enterprise-product-envelope.md` |

## Change Management

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Versioning policy | SemVer and support windows | implemented | `docs/60-versioning-support-policy.md` |
| Release gates | RC checklist and sign-off board | implemented | `docs/61-release-candidate-signoff.md` |
| Migration policy | migration classes and rollback assumptions | implemented | `docs/41-migration-compatibility-policy.md` |
| Release evidence | consolidated enterprise verification report | implemented | `docs/reports/enterprise-verification-latest.md` |
| Release provenance | signed bundles, images, chart, and SBOM workflow | partial | `docs/44-release-provenance-and-verification.md`, `EGA-13` |
| CI deprecation risk | Node 20 cleanup | gap | `EGA-14` |

## Access Control

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Least privilege | repo roles and tenant delegated roles | implemented | `docs/45-tenant-isolation-and-admission-controls.md` |
| Admin bootstrap | bootstrap-token pattern | implemented | `README.md` |
| Token revocation | PAT revoke and revoke-all controls | implemented | `docs/43-security-review-closure-v2.md` |
| Access evidence | tenant role bindings in compliance pack | implemented | `docs/49-compliance-evidence-and-legal-holds.md` |
| Periodic access review | organization-specific review workflow | customer-owned | `docs/62-enterprise-security-whitepaper.md` |

## Audit Logging

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Audit persistence | privileged operations emit audit records | implemented | `docs/10-current-state.md` |
| Audit query | tenant-scoped audit endpoint | implemented | `README.md` |
| Audit export | NDJSON audit export in evidence pack | implemented | `docs/49-compliance-evidence-and-legal-holds.md` |
| Correlation | audit correlation metadata | implemented | `docs/49-compliance-evidence-and-legal-holds.md` |
| SIEM integration | customer ingestion and routing | customer-owned | `docs/62-enterprise-security-whitepaper.md` |

## Incident Response

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| Operations runbook | incident response flow | implemented | `docs/31-operations-howto.md`, `docs/42-operations-dashboard-and-drill-bundle.md` |
| Recovery drills | backup/restore and reliability drills | implemented | `docs/reports/phase6-rto-rpo-drill-latest.md`, `docs/reports/reliability-drill-latest.md` |
| Error catalog | diagnostic error mapping | gap | `EGA-18` |
| Support intake | severity and escalation model | gap | `EGA-27` |

## Vendor And Supply Chain Risk

| Control Theme | Artifortress Control | Status | Evidence |
|---|---|---|---|
| SBOM | CycloneDX SBOM workflow for bundles, images, and chart | partial | `docs/44-release-provenance-and-verification.md`, `EGA-13` |
| Artifact signing | cosign signatures for release bundles, images, chart, and SBOMs | partial | `docs/44-release-provenance-and-verification.md`, `EGA-13` |
| Dependency ownership | NuGet and platform dependency review | customer-owned | `docs/63-procurement-evidence-pack.md` |
| Container signing | signed OCI image distribution | implemented | `.github/workflows/release-provenance.yml` |
| Helm chart signing | signed chart distribution | implemented | `.github/workflows/release-provenance.yml` |

## Gap Register

| Gap | Ticket |
|---|---|
| vulnerability disclosure and patch SLA policy | `EGA-17` |
| SLOs, SLIs, and alert routing guidance | `EGA-11` |
| support bundle collection workflow | `EGA-10` |
| diagnostic error catalog and operator playbooks | `EGA-18` |
| support intake, severity, and escalation model | `EGA-27` |
| real signed-tag release provenance evidence | `EGA-13` |
| GitHub Actions Node 20 deprecation cleanup | `EGA-14` |
| Helm install/upgrade/uninstall certification | `EGA-08` |
| HA deployment validation in Kubernetes reference environment | `EGA-12` |

## Auditor Note

This mapping is intended to accelerate readiness review. Audit conclusions still
depend on the customer deployment, evidence retention, operational practices,
and any third-party attestation process the customer requires.
