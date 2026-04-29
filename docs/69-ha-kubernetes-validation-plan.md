# HA Kubernetes Validation Plan

Last updated: 2026-04-27

## Purpose

This plan defines the Kubernetes evidence required to close `EGA-12`.

`EGA-12` is not considered done until this plan is executed against a real
Kubernetes environment and the resulting evidence report is committed under
`docs/reports`.

## Required Test Environment

Minimum cluster shape:

- Kubernetes cluster with at least `2` schedulable nodes
- namespace dedicated to Kublai validation
- managed or cluster-local PostgreSQL reachable by API and worker
- S3-compatible object storage reachable by API
- ingress or load balancer for API traffic
- Helm available for install/upgrade validation when the release claims Helm
  production support

Minimum Kublai shape:

- `kublai-api`: `3` replicas
- `kublai-worker`: `2` replicas
- readiness probes enabled
- production-like secrets injected from Kubernetes secrets or equivalent
- dashboard and alert thresholds available to operators

## Required Commands

Prepare:

```bash
kubectl create namespace kublai-ha-validation
helm upgrade --install kublai deploy/helm/kublai \
  --namespace kublai-ha-validation \
  --values <environment-values.yaml>
```

Validate rollout:

```bash
kubectl -n kublai-ha-validation rollout status deployment/kublai-api
kubectl -n kublai-ha-validation rollout status deployment/kublai-worker
kubectl -n kublai-ha-validation get pods -o wide
```

Run production preflight:

```bash
API_URL=https://<api-host> \
ADMIN_TOKEN=<admin-token> \
KUBE_NAMESPACE=kublai-ha-validation \
HELM_RELEASE=kublai \
scripts/production-preflight.sh
```

## Validation Scenarios

### Replica Health

Acceptance:

- API has at least `3` ready replicas
- worker has at least `2` ready replicas
- pods are spread across at least `2` nodes when the cluster supports it
- `/health/ready` returns `ready`

### Rolling Restart

Procedure:

```bash
kubectl -n kublai-ha-validation rollout restart deployment/kublai-api
kubectl -n kublai-ha-validation rollout restart deployment/kublai-worker
```

Acceptance:

- API remains available behind readiness gates
- worker backlog does not permanently grow
- `/v1/admin/ops/summary` stabilizes after rollout

### Worker Loss

Procedure:

```bash
kubectl -n kublai-ha-validation scale deployment/kublai-worker --replicas=0
```

Acceptance:

- publish/control-plane paths remain available
- async backlog growth is visible
- restoring workers drains backlog

Restore:

```bash
kubectl -n kublai-ha-validation scale deployment/kublai-worker --replicas=2
```

### API Replica Loss

Procedure:

```bash
kubectl -n kublai-ha-validation scale deployment/kublai-api --replicas=1
```

Acceptance:

- reduced capacity is visible but correctness is preserved
- readiness still gates the remaining replica

Restore:

```bash
kubectl -n kublai-ha-validation scale deployment/kublai-api --replicas=3
```

### Dependency Outage

Procedure:

- simulate PostgreSQL or object-storage unavailability in the validation
  environment

Acceptance:

- readiness becomes `not_ready`
- protected paths fail closed
- recovery returns readiness to `ready`

## Evidence Report Template

Create:

- `docs/reports/ha-kubernetes-validation-latest.md`

Required sections:

- cluster name/context
- date/time
- Kublai commit and image/chart versions
- Kubernetes version
- namespace
- node count and pod placement
- API and worker replica counts
- database and object-storage mode
- production preflight result
- scenario results
- failures and residual risks

## Current Local Status

Local Kubernetes validation has been executed with:

- `kind v0.31.0`
- `kubectl v1.36.0`
- `helm v4.1.4`

Latest evidence:

- `docs/reports/ha-kubernetes-validation-latest.md`

Repeat with:

```bash
make kind-ha-validate
```

The current evidence validates a local kind HA shape. Managed-cloud Kubernetes
validation remains stronger evidence if Kublai later claims a specific
cloud provider or production capacity profile.
