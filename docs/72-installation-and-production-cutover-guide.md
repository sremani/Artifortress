# Installation And Production Cutover Guide

Last updated: 2026-04-28

## Purpose

This guide is the supported end-to-end path from a clean Kubernetes namespace to
a production-ready Kublai deployment.

It covers:

- namespace setup
- secret and Helm values preparation
- ingress and TLS configuration
- one-time migration execution
- API and worker installation
- readiness, operations, smoke, and backup gates
- initial administrator bootstrap
- identity hookup
- dashboard and alert import
- cutover, rollback, and incident criteria

Use this guide with:

- `docs/59-enterprise-product-envelope.md`
- `docs/60-versioning-support-policy.md`
- `docs/61-release-candidate-signoff.md`
- `docs/68-slo-sli-alerting.md`
- `docs/70-production-preflight.md`
- `docs/71-administrator-handbook.md`

Production candidates must use image and Helm chart artifacts from the release
provenance workflow. Verify the OCI image signatures, Helm chart signature,
SBOMs, and checksums before pinning deployment values to image digests or
immutable release tags.

## Required Inputs

Before starting, collect:

- target release version, commit, and image tags or digests
- Kubernetes context for the production cluster
- namespace name, usually `kublai-prod`
- managed PostgreSQL endpoint and credentials
- dedicated managed S3-compatible object-storage endpoint, bucket, and
  credentials
- bootstrap token generated through the organization's secret workflow
- OIDC or SAML identity-provider settings, if federation is enabled
- TLS secret name or certificate-manager ownership for the ingress host
- dashboard and alert destination ownership
- release owner, rollback owner, incident channel, and on-call owner

Do not store real secrets in this repository.

## Clean Cluster Preparation

Select the target cluster and create a dedicated namespace:

```bash
kubectl config use-context <production-context>
kubectl create namespace kublai-prod
```

Label the namespace for ownership and environment tracking:

```bash
kubectl label namespace kublai-prod \
  app.kubernetes.io/part-of=kublai \
  kublai.io/environment=production
```

Confirm no old release is present:

```bash
helm list --namespace kublai-prod
kubectl get all --namespace kublai-prod
```

If resources exist, stop and reconcile ownership before continuing.

## Dependency Preparation

Prepare dependencies before installing the chart:

1. Create or select the production PostgreSQL database.
2. Confirm the database user can connect and run migrations.
3. Create a dedicated object-storage bucket for production.
4. Confirm object-storage credentials can read, write, and perform multipart
   upload operations.
5. Configure ingress controller and DNS ownership.
6. Create or delegate TLS certificate management for the public host.
7. Prepare dashboard import and alert routing.

Dependency validation is not complete until production preflight passes.

Do not use MinIO community artifacts as the production object-store dependency
for `kublai.com`. Use managed S3-compatible storage until the MinIO exit
plan in `docs/74-object-storage-independence-and-minio-exit-plan.md` is closed.

## TLS And Ingress Setup

If TLS is supplied as an existing Kubernetes secret:

```bash
kubectl create secret tls kublai-tls \
  --namespace kublai-prod \
  --cert=/secure/path/tls.crt \
  --key=/secure/path/tls.key
```

If certificate-manager or another controller owns TLS, create the controller's
required issuer/certificate resources before chart installation and set the Helm
ingress annotations accordingly.

Do not cut production DNS to Kublai until readiness, preflight, smoke, and
backup gates pass.

## Helm Values

Create a production values file outside the repository, for example:

```bash
install -m 0600 /dev/null /secure/path/kublai-prod.values.yaml
```

Populate only environment-specific values. Start from
`deploy/helm/kublai/values.yaml`, then override:

```yaml
namespaceOverride: kublai-prod

api:
  replicaCount: 3
  image:
    repository: <internal-registry>/kublai-api
    tag: <release-tag-or-digest>
    pullPolicy: IfNotPresent

worker:
  replicaCount: 2
  image:
    repository: <internal-registry>/kublai-worker
    tag: <release-tag-or-digest>
    pullPolicy: IfNotPresent

ingress:
  enabled: true
  className: nginx
  host: kublai.example.com
  tls:
    enabled: true
    secretName: kublai-tls

config:
  ASPNETCORE_ENVIRONMENT: Production
  ASPNETCORE_URLS: http://0.0.0.0:8086
  Auth__Oidc__Enabled: "true"
  Auth__Oidc__Issuer: https://idp.example.com
  Auth__Oidc__Audience: kublai-api
  Auth__Oidc__JwksUrl: https://idp.example.com/.well-known/jwks.json
  Auth__Oidc__JwksRefreshIntervalSeconds: "300"
  Auth__Oidc__JwksRefreshTimeoutSeconds: "10"
  Auth__Oidc__JwksMaxStalenessSeconds: "900"
  Auth__Oidc__RoleMappings: groups|af-admins|*|admin
  Auth__Saml__Enabled: "false"
  ObjectStorage__Endpoint: https://object-storage.example.com
  ObjectStorage__Bucket: kublai-prod

secretConfig:
  ConnectionStrings__Postgres: Host=<postgres-host>;Port=5432;Username=<username>;Password=<password>;Database=kublai
  Auth__BootstrapToken: <redacted-production-bootstrap-token>
  ObjectStorage__AccessKey: <redacted-access-key>
  ObjectStorage__SecretKey: <redacted-secret-key>
```

If SAML is enabled, also set the SAML metadata, issuer, service-provider entity
id, role mappings, refresh windows, and optional static metadata fallback
documented in `docs/30-deployment-config-reference.md`.

## Render And Review

Render the chart before applying it:

```bash
helm template kublai deploy/helm/kublai \
  --namespace kublai-prod \
  -f /secure/path/kublai-prod.values.yaml \
  >/tmp/kublai-prod-rendered.yaml
```

Review:

- API replicas are at least `3`
- worker replicas are at least `2`
- ingress host and TLS secret are correct
- image references match the release candidate
- secrets are present only in Kubernetes Secret manifests
- no placeholder values remain
- PDB and network policy are enabled unless an exception is recorded

## Database Migration Gate

Before installing or upgrading production traffic, create a backup:

```bash
make db-backup
```

Apply migrations once from the approved release workspace:

```bash
make db-migrate
```

For upgrade releases, confirm the migration class and rollback path in:

- `docs/41-migration-compatibility-policy.md`
- `docs/48-upgrade-compatibility-matrix.md`
- `docs/25-upgrade-rollback-runbook.md`

If migration fails, do not install the new release into production traffic.
Follow rollback guidance in `docs/25-upgrade-rollback-runbook.md`.

## Install

Install or update Kublai:

```bash
helm upgrade --install kublai deploy/helm/kublai \
  --namespace kublai-prod \
  --create-namespace \
  -f /secure/path/kublai-prod.values.yaml \
  --wait \
  --timeout 10m
```

Verify Kubernetes state:

```bash
kubectl get deploy,pod,svc,ingress,pdb,networkpolicy \
  --namespace kublai-prod

kubectl rollout status deployment/kublai-api \
  --namespace kublai-prod \
  --timeout=5m

kubectl rollout status deployment/kublai-worker \
  --namespace kublai-prod \
  --timeout=5m
```

If rollout does not complete, keep production DNS or traffic on the previous
environment and inspect pod events, readiness output, and logs.

## Readiness Gate

Check health from the operator network or through the ingress path:

```bash
curl -sS https://kublai.example.com/health/live
curl -sS https://kublai.example.com/health/ready
```

Pass criteria:

- `/health/live` responds
- `/health/ready` top-level `status` is `ready`
- `postgres` dependency is ready
- `object_storage` dependency is ready
- OIDC or SAML trust-material status matches the identity mode claimed for the
  environment

If readiness is `degraded`, do not cut traffic until the release owner accepts
the warning in sign-off. If readiness is `not_ready`, block cutover.

## Initial Administrator Bootstrap

Issue a short-lived administrative PAT only after the API is reachable:

```bash
curl -sS \
  -H "X-Bootstrap-Token: <bootstrap-token>" \
  -H "Content-Type: application/json" \
  -X POST https://kublai.example.com/v1/auth/pats \
  -d '{"subject":"platform-admin","scopes":["repo:*:admin"],"ttlMinutes":60}'
```

Store the returned token in the operator's secret handling workflow, then verify
identity:

```bash
curl -sS \
  -H "Authorization: Bearer <admin-token>" \
  https://kublai.example.com/v1/auth/whoami
```

Reduce bootstrap exposure after OIDC or SAML administration is validated. Use
`docs/71-administrator-handbook.md` for PAT governance and offboarding details.

## Identity Hookup Gate

For OIDC:

1. confirm issuer and audience match the identity provider
2. confirm configured signing mode is available
3. validate a federated admin token with `/v1/auth/whoami`
4. validate claim-role mappings by reading or creating an allowed resource
5. confirm unauthorized audience or unmapped role attempts fail

For SAML:

1. validate `GET /v1/auth/saml/metadata`
2. validate ACS exchange against the configured issuer and service-provider
   entity id
3. validate mapped roles from a non-bootstrap principal
4. confirm fallback and disable controls are documented for the cutover

Use `docs/37-phase7-runbook.md` and `docs/38-phase7-rollout-gates.md` for
identity troubleshooting and fallback.

## Operations Summary Gate

Check privileged operations posture:

```bash
curl -sS \
  -H "Authorization: Bearer <admin-token>" \
  https://kublai.example.com/v1/admin/ops/summary \
  >/tmp/kublai-ops-summary.json
```

Pass criteria:

- readiness status is `ready`
- pending outbox and search backlog are within alert thresholds
- failed search jobs are not growing
- incomplete GC runs are absent
- recent policy timeouts are absent or accepted by release owner

Alert thresholds are defined in `deploy/enterprise-ops-alert-thresholds.yaml`
and summarized in `docs/68-slo-sli-alerting.md`.

## Dashboard And Alert Setup

Import dashboard and alert assets before cutover:

- dashboard: `deploy/grafana-kublai-operations-dashboard.json`
- alert thresholds: `deploy/enterprise-ops-alert-thresholds.yaml`
- operational drill bundle: `docs/42-operations-dashboard-and-drill-bundle.md`

Cutover cannot proceed until:

- dashboard panels are visible for the production environment
- alert destinations page or ticket according to `docs/68-slo-sli-alerting.md`
- on-call staff can query `/v1/admin/ops/summary`
- a support bundle can be collected without leaking secrets

## Smoke Test Gate

Run a minimal production smoke using a non-bootstrap administrator or a
short-lived bootstrap-issued token:

1. `GET /v1/auth/whoami`
2. `GET /v1/repos`
3. create or read a designated smoke repository
4. validate repository binding readback
5. perform a small upload/download or existing blob read where allowed
6. query `GET /v1/audit` for privileged actions
7. export a small compliance evidence pack if the environment claims auditor
   workflow readiness

Do not run destructive lifecycle actions as smoke unless the target repository
and version are explicitly disposable.

## Production Preflight Gate

Run the production preflight after Helm install, readiness checks, identity
validation, dashboard wiring, and smoke tests:

```bash
API_URL=https://kublai.example.com \
ADMIN_TOKEN=<admin-token> \
ConnectionStrings__Postgres='<redacted>' \
ObjectStorage__Endpoint=https://object-storage.example.com \
ObjectStorage__Bucket=kublai-prod \
Auth__BootstrapToken='<redacted>' \
ASPNETCORE_ENVIRONMENT=Production \
KUBE_NAMESPACE=kublai-prod \
HELM_RELEASE=kublai \
scripts/production-preflight.sh
```

Pass criteria:

- `docs/reports/production-preflight-latest.md` exists
- `overall status: PASS`
- `failure count: 0`
- warnings are explicitly accepted in the release sign-off board or resolved

## Backup And Drill Gate

Before production traffic is cut over, confirm current evidence:

- `docs/reports/enterprise-verification-latest.md`
- `docs/reports/phase6-rto-rpo-drill-latest.md`
- `docs/reports/upgrade-compatibility-drill-latest.md`
- `docs/reports/reliability-drill-latest.md`
- `docs/reports/ha-kubernetes-validation-latest.md`

At minimum, a fresh backup must exist and the latest backup/restore drill must
show `PASS`. If these are missing, block cutover.

## Cutover Decision

Cut production traffic only when all gates pass:

- release sign-off has no unresolved blocker
- Helm rollout is complete
- readiness is stable and `ready`
- `/v1/admin/ops/summary` is healthy
- identity hookup is validated
- dashboard and alert routing are active
- smoke test passes
- production preflight passes
- backup and drill evidence is current
- rollback owner confirms rollback path and latest backup location

Recommended traffic sequence:

1. point a low-risk validation host or internal DNS alias at the ingress
2. repeat readiness and smoke checks
3. shift production DNS or load-balancer traffic
4. monitor actively for at least `60` minutes
5. keep hourly checks for the first `24` hours

## Rollback

Rollback immediately if any of these occur during cutover:

- `/health/ready` becomes persistently `not_ready`
- required dependency readiness fails
- identity trust material fails closed unexpectedly
- migration or correctness regression is discovered
- upload/download or repository read paths fail smoke
- backlog exceeds critical alert thresholds
- legal hold, audit, or tenant isolation behavior is suspect

Rollback procedure:

1. stop or route traffic away from the new release
2. run `helm rollback kublai <previous-revision> --namespace kublai-prod`
3. restore previous image or values if Helm rollback is not sufficient
4. restore database from the approved backup only when required by the migration
   class and rollback runbook
5. verify `/health/live`, `/health/ready`, and `/v1/admin/ops/summary`
6. open an incident follow-up with evidence and next action owner

Use `docs/25-upgrade-rollback-runbook.md` for the authoritative rollback
procedure.

## Incident Procedure

If cutover triggers an incident:

1. classify severity using `docs/67-support-intake-and-escalation.md`
2. capture readiness output and operations summary
3. collect a redacted support bundle:

```bash
API_URL=https://kublai.example.com \
ADMIN_TOKEN=<admin-token> \
scripts/support-bundle.sh
```

4. keep traffic off the affected release if readiness is `not_ready`
5. use `docs/66-diagnostic-error-catalog.md` for symptom-specific actions
6. do not declare recovery until readiness, backlog, and smoke checks stabilize

## Cutover Evidence Archive

Archive these artifacts with the release record:

- release version, commit, chart version, image tags or digests
- rendered Helm manifest review notes
- migration output and backup path
- readiness and ops summary snapshots
- smoke test notes
- production preflight report
- dashboard and alert confirmation
- drill and verification report references
- exception records, if any
- rollback owner and rollback decision notes

This archive feeds the release sign-off process in
`docs/61-release-candidate-signoff.md`.
