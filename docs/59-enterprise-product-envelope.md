# Enterprise Product Envelope

Last updated: 2026-04-27

## Purpose

This document defines the supported enterprise product envelope for an
Artifortress enterprise GA candidate.

The envelope is intentionally conservative. It states what Artifortress can
support based on repository evidence today, and it identifies claims that remain
outside the current support boundary.

## Supported Product Shape

Artifortress is supported as a self-hosted artifact repository control plane
with:

- HTTP API for identity, repository administration, upload/download, package
  version lifecycle, manifest publication, policy/quarantine, search, audit,
  compliance evidence, and lifecycle administration
- F#/.NET API and worker services
- PostgreSQL as the source of truth for metadata and control-plane state
- S3-compatible object storage for blob bytes
- transactional outbox and worker processing for async search/index workflows
- Kubernetes/Helm-oriented production deployment assets
- Docker Compose local development and verification stack

## Supported Runtime Baseline

Application:
- .NET SDK `10.0.102` or newer patch release through `global.json`
  `rollForward=latestPatch`
- target framework `net10.0`
- F# language version `10.0`

Core dependencies:
- PostgreSQL compatible with the repository's migration SQL and current local
  verification stack
- S3-compatible object storage with multipart upload support
- Kubernetes for the production deployment shape

Local verification stack:
- Docker Compose services for Postgres, MinIO, Redis, OTel Collector, and
  Jaeger
- Redis is part of the local/demo stack but is not on the current production
  critical path

## Supported Deployment Topologies

### Development

Supported for local development and verification only:
- Docker Compose dependency stack
- API and worker run as local `dotnet` processes
- local MinIO bucket for object storage

### Staging

Supported as a pre-production environment:
- dedicated database
- dedicated object-storage bucket
- no shared data-plane resources with production
- at least one API instance and one worker instance
- same migration, readiness, smoke, and rollback workflow as production

### Production

Supported production shape:
- `artifortress-api` with at least `3` replicas
- `artifortress-worker` with at least `2` replicas
- replicas spread across at least `2` availability zones when the underlying
  platform supports it
- regional load balancer or ingress in front of API replicas
- managed HA PostgreSQL or equivalent durability/failover posture
- regional durable S3-compatible object storage
- dedicated bucket per environment
- readiness-gated traffic
- backup/restore, reliability, and upgrade drills current before cutover

## Supported Scale Profiles

Supported scale profiles are defined in
`docs/53-performance-soak-and-capacity-guide.md`.

Current support boundary:
- local calibration evidence is suitable for regression detection
- initial small/medium/large profile guidance is supported for planning
- production-like capacity certification remains a separate P1 GA ticket
  (`EGA-24`)

## Supported Identity Modes

Supported identity and authorization capabilities:
- bootstrap token for initial administration
- PAT issue, revoke, policy, last-used tracking, and blast-radius controls
- OIDC bearer-token validation with configured issuer/audience and trust
  material controls
- SAML metadata and ACS assertion exchange with signed assertion validation
- tenant-scoped role bindings and delegated tenant roles
- repo-scoped RBAC for repository operations

Support assumptions:
- customers own identity provider availability, certificate lifecycle, and
  issuer/audience configuration
- trust-material degradation must be handled according to readiness and ops
  surfaces
- administrative tokens and secrets must be injected through deployment
  secret-management facilities, not committed to git

## Supported Compliance And Governance Posture

Supported controls:
- tenant-scoped audit query
- audit correlation metadata
- immutable audit export
- compliance evidence pack endpoint
- legal hold controls that block destructive lifecycle actions
- governance approval records and dual-control workflow support
- policy/quarantine controls for protected artifact workflows

Support boundary:
- Artifortress provides product evidence and control surfaces
- customers remain responsible for organization-specific control mapping,
  auditor sign-off, retention policy configuration, and production access review

## Supported Upgrade And Rollback Paths

Supported upgrade paths are defined in
`docs/48-upgrade-compatibility-matrix.md`.

Current support includes:
- forward upgrade from the listed supported baselines to current head
- application-only rollback when schema compatibility remains intact
- restore-based rollback for paths that break schema compatibility
- migration classes and downtime expectations from
  `docs/41-migration-compatibility-policy.md`

Unsupported:
- arbitrary downgrade to historical binary revisions without restore
- ad hoc destructive reverse SQL during incident response
- support for baselines older than the published compatibility matrix

## Supported Operations Evidence

The following evidence is required before an enterprise production declaration:

- `make verify-enterprise` passing
- `docs/reports/enterprise-verification-latest.md`
- `docs/reports/phase6-rto-rpo-drill-latest.md`
- `docs/reports/upgrade-compatibility-drill-latest.md`
- `docs/reports/reliability-drill-latest.md`
- `docs/reports/ha-kubernetes-validation-latest.md`
- `docs/reports/search-soak-drill-latest.md`
- `docs/reports/performance-workflow-baseline-latest.md`
- `docs/reports/performance-soak-latest.md`

## Explicitly Unsupported Claims

The current enterprise envelope does not claim support for:

- multi-region active/active metadata writes
- cross-region worker coordination for a single tenant control plane
- zero-data-loss rollback of destructive schema changes without restore
- object-store portability guarantees across vendors without validation
- air-gapped/offline installation
- certified production capacity beyond currently published evidence
- Helm install/upgrade/uninstall certification job
- package-manager protocol compatibility beyond the documented HTTP API surface
- paid-customer support intake, severity, and escalation model
- reference Terraform/OpenTofu deployment module

Items above are either deferred or tracked separately in the enterprise GA board.

## Source Of Truth

When support documents disagree, resolve in this order:

1. `docs/59-enterprise-product-envelope.md`
2. `docs/60-versioning-support-policy.md`
3. `docs/61-release-candidate-signoff.md`
4. `docs/48-upgrade-compatibility-matrix.md`
5. `docs/41-migration-compatibility-policy.md`
6. implementation and release artifacts for the target tag
