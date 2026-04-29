# Support Bundle Workflow

Last updated: 2026-04-27

## Purpose

This document defines the supported Kublai support bundle workflow.

The goal is to give operators and support staff a repeatable diagnostic package
without requiring direct database access or sharing raw secrets.

## Bundle Command

From the repository root:

```bash
scripts/support-bundle.sh
```

Optional environment:

```bash
API_URL=https://kublai.example.com \
ADMIN_TOKEN=<admin-token> \
scripts/support-bundle.sh
```

Optional output overrides:

```bash
SUPPORT_BUNDLE_DIR=/tmp/af-support \
SUPPORT_BUNDLE_ARCHIVE=/tmp/af-support.tar.gz \
scripts/support-bundle.sh
```

## Collected Evidence

The bundle includes:

- generation timestamp
- host and OS metadata
- git branch and commit
- .NET SDK/runtime versions
- redacted configuration shape
- git working tree status
- Docker and Docker Compose status when available
- `/health/live` snapshot when `API_URL` is provided
- `/health/ready` snapshot when `API_URL` is provided
- `/v1/admin/ops/summary` snapshot when `API_URL` and `ADMIN_TOKEN` are provided
- current verification and drill reports under `docs/reports`

## Redaction Policy

The support bundle must not emit raw secret values.

The configuration shape file records only whether known configuration keys are
present in the bundle collection environment. It does not emit values for:

- database connection strings
- bootstrap tokens
- OIDC shared secrets
- OIDC JWKS JSON
- SAML metadata XML
- object-storage access keys
- object-storage secret keys
- admin bearer tokens

Operators must still review the generated archive before sharing it outside
their organization.

## When To Collect A Bundle

Collect a bundle when:

- readiness is `not_ready`
- readiness is persistently `degraded`
- upload/download behavior is failing
- worker backlog is growing
- search jobs are failed or stale
- backup/restore or upgrade drill fails
- release candidate evidence needs support review
- support asks for one during incident triage

## Intake Checklist

When opening a support request, include:

- support bundle archive
- customer-visible impact
- start time and timezone
- affected environment
- release version or commit
- recent deployment, migration, identity, storage, or network changes
- whether production traffic is currently affected
- whether rollback or failover has been attempted

## Safety Notes

- Do not attach raw database dumps to support requests unless explicitly
  requested through a secure channel.
- Do not attach raw object-storage contents.
- Do not paste admin bearer tokens into tickets.
- Do not run the bundle from a shell containing unrelated secret-bearing
  environment variables unless the archive is reviewed before sharing.

## Follow-Up Automation

Future support-bundle improvements may include:

- structured JSON summary
- explicit secret scanner on bundle output
- Kubernetes pod/event/log collection
- database migration-state probe
- signed bundle manifest
