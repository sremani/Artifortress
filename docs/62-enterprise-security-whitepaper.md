# Enterprise Security Whitepaper

Last updated: 2026-04-27

## Purpose

This whitepaper summarizes the Artifortress enterprise security model for
security review, procurement review, and production deployment planning.

It is not a certification claim. It maps the product's security posture to
implemented controls, repository evidence, and explicit customer
responsibilities.

## Product Summary

Artifortress is a self-hosted artifact repository control plane. It separates:

- metadata, identity, audit, and state transitions in PostgreSQL
- artifact bytes in S3-compatible object storage
- asynchronous indexing and lifecycle work through a PostgreSQL-backed outbox
  and worker model

Primary production components:

- `artifortress-api`
- `artifortress-worker`
- PostgreSQL
- S3-compatible object storage
- Kubernetes/Helm deployment assets

The supported product envelope is defined in
`docs/59-enterprise-product-envelope.md`.

## Security Design Principles

Artifortress security is built around these principles:

- fail closed when identity, trust material, authorization, or dependency state
  is ambiguous
- keep control-plane truth in PostgreSQL and blob bytes in object storage
- persist token hashes only, never plaintext PAT values
- require tenant and repository scope checks before privileged actions
- keep background jobs tenant-owned and replay safe
- make audit, compliance evidence, and legal hold controls first-class APIs
- produce signed release artifacts and SBOM evidence before promotion

## Authentication

Supported authentication modes:

- bootstrap token for initial administration
- PAT bearer tokens
- OIDC bearer-token validation
- SAML metadata and ACS assertion exchange

PAT controls:

- token material is returned only at issuance time
- token hashes are persisted
- revocation is supported
- last-used tracking and inventory controls are implemented
- revoke-by-subject and revoke-all controls limit blast radius
- issuance policy can constrain token lifetime and risk

OIDC/SAML controls:

- issuer and audience checks are enforced
- OIDC JWKS and SAML trust material use bounded refresh and stale-fallback
  controls
- SAML assertions require cryptographic signature validation against configured
  IdP signing certificates
- degraded trust-material state is exposed through readiness and operational
  surfaces

Evidence:

- `docs/36-phase7-identity-integration-tickets.md`
- `docs/37-phase7-runbook.md`
- `docs/38-phase7-rollout-gates.md`
- `docs/43-security-review-closure-v2.md`

Customer responsibilities:

- operate the IdP
- rotate signing material according to enterprise policy
- configure issuer, audience, claim mapping, and metadata endpoints correctly
- monitor trust-material degraded states

## Authorization

Authorization uses tenant-scoped and repository-scoped controls:

- tenant-resolved authenticated principals
- repo-scoped RBAC for repository actions
- tenant role bindings for delegated tenant administration and auditing
- platform admin-equivalent access for global administrative surfaces
- policy/quarantine controls for protected artifact workflows

Authorization is paired with tenant-filtered data access. A syntactically valid
repo scope is not sufficient to cross tenant boundaries.

Evidence:

- `docs/45-tenant-isolation-and-admission-controls.md`
- `docs/49-compliance-evidence-and-legal-holds.md`
- `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`

## Tenant Isolation

Tenant isolation invariants:

- mutable domain aggregates carry tenant ownership
- authentication resolves tenant before authorization
- shared identifiers do not imply shared visibility
- search, quarantine, audit, repository reads, and background jobs are
  tenant-filtered
- negative tests cover cross-tenant access attempts

Admission controls:

- concurrent upload session limits
- pending search job limits
- logical storage limit field for staged rollout
- deterministic over-limit responses

Evidence:

- `docs/45-tenant-isolation-and-admission-controls.md`
- `db/migrations/0011_tenant_isolation_and_admission_controls.sql`
- `docs/reports/performance-soak-latest.md`

## Data Protection

Metadata:

- PostgreSQL is the source of truth
- schema migrations are versioned under `db/migrations`
- migration state is tracked through `schema_migrations`
- backup and restore are supported and drilled

Blob bytes:

- stored in S3-compatible object storage
- upload commit validates digest and length
- dedupe uses content-addressed digest identity
- object-storage availability is part of readiness

Secrets:

- secrets are environment-injected
- plaintext token values are not persisted
- sensitive fields are redacted from audit and worker error surfaces
- secret-bearing values must not be committed to git

Evidence:

- `docs/24-security-review-closure.md`
- `docs/43-security-review-closure-v2.md`
- `docs/reports/phase6-rto-rpo-drill-latest.md`

Customer responsibilities:

- enable encryption at rest for managed PostgreSQL and object storage
- manage TLS termination and network policies
- manage secret injection and rotation
- configure bucket policy and backup retention

## Audit And Compliance

Supported controls:

- tenant-scoped audit query
- audit correlation metadata
- immutable audit export
- compliance evidence pack endpoint
- governance policy snapshots
- role binding evidence
- legal hold listing
- governance approval evidence

Legal hold behavior:

- active legal holds prevent hard deletion of protected artifact versions
- GC respects held versions and their rooted blobs
- hold release is explicit and audited

Evidence:

- `docs/49-compliance-evidence-and-legal-holds.md`
- `docs/47-background-job-replay-safety.md`
- `docs/reports/reliability-drill-latest.md`

## Release And Supply Chain

Release evidence includes:

- signed source tag requirement
- release bundles
- signed API and worker OCI images
- signed Helm chart package
- SHA256 sums
- CycloneDX SBOMs for bundles, images, and chart
- cosign signatures and certificates
- local release artifact verification helper

Evidence:

- `.github/workflows/release-provenance.yml`
- `scripts/verify-release-artifacts.sh`
- `docs/44-release-provenance-and-verification.md`
- `docs/reports/release-provenance-latest.md`

Known residual release risks:

- no residual release-provenance runtime deprecation risk is known after
  `EGA-14`

## Availability And Resilience

Supported resilience posture:

- API replicas are stateless
- worker replicas use `FOR UPDATE SKIP LOCKED` and lease-based reclaim behavior
- readiness checks cover PostgreSQL and object storage
- operational summary surfaces backlog and lifecycle health signals
- backup/restore and reliability drills are executable
- upgrade compatibility drills verify supported schema baselines

Evidence:

- `docs/40-ha-topology-and-failure-domains.md`
- `docs/42-operations-dashboard-and-drill-bundle.md`
- `docs/reports/enterprise-verification-latest.md`
- `docs/reports/upgrade-compatibility-drill-latest.md`
- `docs/reports/reliability-drill-latest.md`

Unsupported resilience claims:

- multi-region active/active metadata writes
- cross-region worker coordination
- zero-data-loss rollback of destructive schema changes without restore

## Threat Model Summary

Primary enterprise threat classes and current closure:

| Threat | Control Summary | Status |
|---|---|---|
| unsigned SAML assertion acceptance | signed assertion validation against IdP certificates | closed |
| trust material drift | bounded refresh, staleness limits, degraded signaling | closed |
| token sprawl | PAT policy, inventory, revoke-by-subject, revoke-all | closed |
| secret leakage | centralized redaction coverage for audit and worker errors | closed |
| replay or restart corruption | idempotency and lease reclaim drills | closed |
| unverifiable releases | signed-tag provenance workflow, SBOMs, cosign verification helper | closed |

Open P0/P1 security blockers in the current security closure document: `0`.

## Customer Security Responsibilities

Customers remain responsible for:

- network segmentation and firewall policy
- TLS certificates and ingress policy
- IdP operations and key rotation
- database and object-storage encryption configuration
- production backup retention and restore governance
- user access review
- SIEM ingestion and alert routing
- organization-specific compliance mapping and auditor sign-off

## References

- `docs/43-security-review-closure-v2.md`
- `docs/59-enterprise-product-envelope.md`
- `docs/60-versioning-support-policy.md`
- `docs/61-release-candidate-signoff.md`
- `docs/63-procurement-evidence-pack.md`
- `docs/64-soc2-control-mapping.md`
