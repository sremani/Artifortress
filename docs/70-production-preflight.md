# Production Preflight

Last updated: 2026-04-27

## Purpose

This document defines the production preflight workflow for Kublai.

Preflight is the deterministic cutover gate that verifies required files,
configuration shape, runtime health, operational evidence, and optional
Kubernetes/Helm state before declaring an environment production-ready.

## Command

```bash
API_URL=https://kublai.example.com \
ADMIN_TOKEN=<admin-token> \
ConnectionStrings__Postgres='<redacted>' \
ObjectStorage__Endpoint=https://object-storage.example.com \
ObjectStorage__Bucket=kublai-prod \
Auth__BootstrapToken='<redacted>' \
ASPNETCORE_ENVIRONMENT=Production \
scripts/production-preflight.sh
```

Optional Kubernetes and Helm checks:

```bash
KUBE_NAMESPACE=kublai-prod \
HELM_RELEASE=kublai \
scripts/production-preflight.sh
```

Offline documentation/evidence smoke:

```bash
PREFLIGHT_OFFLINE=true \
API_URL=https://kublai.example.com \
ADMIN_TOKEN=redacted \
ConnectionStrings__Postgres=redacted \
ObjectStorage__Endpoint=https://object-storage.example.com \
ObjectStorage__Bucket=kublai-prod \
Auth__BootstrapToken=redacted \
scripts/production-preflight.sh
```

## Output

Default report:

- `docs/reports/production-preflight-latest.md`

Override:

```bash
PREFLIGHT_REPORT_PATH=/tmp/production-preflight.md scripts/production-preflight.sh
```

## Required Checks

Preflight validates:

- deployment assets exist
- enterprise support and release docs exist
- required evidence reports exist
- Helm install/upgrade/uninstall certification evidence exists
- required runtime configuration keys are present
- OIDC/SAML enabled modes have required trust anchors
- `/health/live` responds
- `/health/ready` is `ready`
- `/v1/admin/ops/summary` responds with the provided admin token
- ingress URL uses HTTPS
- Kubernetes deployment state when `kubectl` and `KUBE_NAMESPACE` are available
- Helm release state when `helm`, `HELM_RELEASE`, and `KUBE_NAMESPACE` are
  available

The script does not print secret values.

## Pass Criteria

The preflight report must have:

- `overall status: PASS`
- `failure count: 0`

Warnings are allowed only when the release sign-off board accepts the related
support-envelope implication.

Examples:

- warning for disabled SAML is acceptable when SAML support is not claimed for
  that environment
- warning for unavailable `kubectl` is not acceptable when claiming Kubernetes
  HA validation evidence

## Failure Handling

If preflight fails:

1. do not cut traffic to the environment
2. inspect `docs/reports/production-preflight-latest.md`
3. fix the failing check
4. rerun preflight
5. attach the final passing report to release or cutover evidence

## Release Gate

Production cutover requires:

- passing preflight report
- passing enterprise verification report
- current backup/restore drill report
- current upgrade compatibility drill report
- current Helm certification report
- release candidate sign-off board with no unresolved blockers
