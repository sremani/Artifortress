# Procurement Evidence Pack

Last updated: 2026-04-28

## Purpose

This document is the procurement and security-review evidence index for an
Artifortress enterprise GA candidate.

It gathers the materials a buyer, security reviewer, platform team, or legal
reviewer usually needs before approving production use.

## Executive Summary

Artifortress is a self-hosted artifact repository control plane designed for:

- correctness-first artifact lifecycle operations
- tenant-aware repository administration
- auditable access and governance workflows
- Postgres-backed metadata durability
- S3-compatible blob storage
- enterprise identity integration
- operational drill evidence and release provenance

Current repository evidence shows the implementation can pass local enterprise
verification, integration tests, upgrade drills, reliability drills, and
performance soak drills.

## Evidence Index

| Review Area | Evidence |
|---|---|
| Product envelope | `docs/59-enterprise-product-envelope.md` |
| Version/support policy | `docs/60-versioning-support-policy.md` |
| Release sign-off | `docs/61-release-candidate-signoff.md` |
| Security model | `docs/62-enterprise-security-whitepaper.md` |
| SOC 2-style controls | `docs/64-soc2-control-mapping.md` |
| Architecture | `docs/01-system-architecture.md`, `Architecture.md` |
| Deployment | `docs/27-deployment-architecture.md`, `docs/28-deployment-howto-staging.md`, `docs/29-deployment-howto-production.md` |
| Operations | `docs/31-operations-howto.md`, `docs/42-operations-dashboard-and-drill-bundle.md` |
| Admin CLI | `docs/76-admin-cli-operator-workflows.md`, `tools/Artifortress.AdminCli` |
| HA and failure domains | `docs/40-ha-topology-and-failure-domains.md` |
| Upgrade/rollback | `docs/25-upgrade-rollback-runbook.md`, `docs/41-migration-compatibility-policy.md`, `docs/48-upgrade-compatibility-matrix.md` |
| Security closure | `docs/43-security-review-closure-v2.md` |
| Release provenance | `docs/44-release-provenance-and-verification.md`, `.github/workflows/release-provenance.yml` |
| Tenant isolation | `docs/45-tenant-isolation-and-admission-controls.md` |
| Dependency failures | `docs/46-dependency-failure-mode-matrix.md` |
| Replay safety | `docs/47-background-job-replay-safety.md` |
| Compliance and legal hold | `docs/49-compliance-evidence-and-legal-holds.md` |
| Capacity | `docs/53-performance-soak-and-capacity-guide.md` |
| Enterprise verification | `docs/reports/enterprise-verification-latest.md` |

## Standard Questionnaire Answers

### Deployment Model

Artifortress is self-hosted. The current production-oriented deployment shape is
Kubernetes/Helm with external PostgreSQL and S3-compatible object storage.

Artifortress is not currently offered as a hosted SaaS in this repository.

### Data Storage

Metadata and control-plane state are stored in PostgreSQL. Artifact bytes are
stored in S3-compatible object storage. The product intentionally separates
control-plane truth from blob bytes.

### Customer Data Types

Artifortress stores:

- repository metadata
- package/version metadata
- artifact entry metadata and checksums
- blob digests and object-storage keys
- authentication and authorization metadata
- audit events
- compliance evidence metadata
- legal hold and governance records

Artifact bytes are customer-controlled content stored in object storage.

### Authentication

Supported authentication modes:

- bootstrap token
- PAT bearer tokens
- OIDC bearer tokens
- SAML assertion exchange

### Authorization

Authorization includes tenant-scoped and repository-scoped controls. Repository
operations require repo-scoped RBAC. Tenant administrative workflows use tenant
role bindings and delegated roles.

### Tenant Isolation

Tenant boundaries are enforced through tenant ownership on mutable aggregates,
tenant-resolved authentication, tenant-filtered data access, and cross-tenant
negative integration coverage.

### Encryption

The repository expects customers to enable encryption at rest in PostgreSQL and
object storage and to provide TLS at ingress/load-balancer boundaries.

Application-level token storage persists hashes rather than plaintext PATs.

### Audit And Evidence

Artifortress exposes tenant-scoped audit query, immutable audit export,
compliance evidence pack generation, legal hold listing, and governance approval
evidence.

### Backups And Restore

Backup and restore procedures are scripted and drilled. Current evidence is in
`docs/reports/phase6-rto-rpo-drill-latest.md`.

### Release Provenance

The release provenance workflow produces release bundles, signed OCI images, a
signed Helm chart package, checksums, SBOMs, and cosign verification material.
The latest signed-tag evidence is in
`docs/reports/release-provenance-latest.md`.

### Vulnerability Handling

Security patch expectations are defined in
`docs/60-versioning-support-policy.md`.

External vulnerability intake, severity classification, patch targets, and
coordinated advisory workflow are defined in `SECURITY.md` and
`docs/75-vulnerability-disclosure-and-patch-sla.md`.

### Support

The versioning and support-window policy is documented in
`docs/60-versioning-support-policy.md`. Paid-customer support intake, severity,
and escalation workflow is documented in
`docs/67-support-intake-and-escalation.md`.

## Subprocessors And External Dependencies

Runtime dependencies:

- PostgreSQL
- S3-compatible object storage
- customer identity provider for OIDC/SAML
- Kubernetes platform and ingress/load balancer

Development/observability dependencies:

- Redis in local/demo stack
- OpenTelemetry Collector
- Jaeger

Release/development dependencies:

- GitHub Actions
- NuGet package ecosystem
- cosign/Sigstore tooling for release verification

Customers are responsible for reviewing vendor risk for their selected managed
database, object storage, Kubernetes, identity provider, and observability
providers.

## Required Evidence Before Procurement Approval

Before procurement approval for enterprise GA, the release record should include:

- signed RC or GA tag
- completed `docs/61-release-candidate-signoff.md`
- `docs/reports/enterprise-verification-latest.md`
- release provenance output from downloaded artifacts
- security whitepaper review
- product envelope review
- support policy review
- known issues and unsupported claims list
- any exceptions with owner and expiration

## Current Gaps To Track

Open procurement-relevant gaps:

- `EGA-20`: tenant onboarding and offboarding workflow
- `EGA-21`: cloud-specific production examples
- `EGA-22`: air-gapped/offline install plan
- `EGA-23`: backup/restore and upgrade drills against release artifacts
- `EGA-24`: capacity certification on non-local infrastructure
- `EGA-25`: package-format compatibility strategy

These gaps do not invalidate the current security and release evidence, but
they narrow which enterprise claims should be made during procurement review.
