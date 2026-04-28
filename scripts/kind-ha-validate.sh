#!/usr/bin/env bash
set -euo pipefail

export PATH="/home/srikanth/bin:${PATH}"

cluster_name="${KIND_CLUSTER_NAME:-artifortress-ha}"
namespace="${KIND_NAMESPACE:-artifortress-ha-validation}"
release_name="${HELM_RELEASE:-artifortress}"
report_path="${HA_K8S_REPORT_PATH:-docs/reports/ha-kubernetes-validation-latest.md}"
api_port="${KIND_API_PORT:-18086}"
bootstrap_token="${KIND_BOOTSTRAP_TOKEN:-kind-ha-bootstrap}"

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
mkdir -p "$(dirname "$report_path")"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

require_command docker
require_command kind
require_command kubectl
require_command helm

if ! kind get clusters | grep -Fxq "$cluster_name"; then
  kind create cluster --config deploy/kind/kind-ha.yaml
fi

kubectl config use-context "kind-${cluster_name}" >/dev/null

docker build -f deploy/kind/Dockerfile.api -t artifortress-api:kind-ha .
docker build -f deploy/kind/Dockerfile.worker -t artifortress-worker:kind-ha .

kind load docker-image --name "$cluster_name" artifortress-api:kind-ha
kind load docker-image --name "$cluster_name" artifortress-worker:kind-ha

kubectl create namespace "$namespace" --dry-run=client -o yaml | kubectl apply -f -
kubectl -n "$namespace" apply -f deploy/kind/dependencies.yaml
kubectl -n "$namespace" rollout status deployment/postgres --timeout=180s
kubectl -n "$namespace" rollout status deployment/minio --timeout=180s

bucket_job="minio-bootstrap-$(date -u +%s)"
kubectl -n "$namespace" create job "$bucket_job" \
  --image=minio/mc:latest \
  -- sh -c "mc alias set local http://minio:9000 artifortress artifortress && mc mb -p local/artifortress-dev"
kubectl -n "$namespace" wait --for=condition=complete "job/${bucket_job}" --timeout=120s

kubectl -n "$namespace" exec deploy/postgres -- psql -v ON_ERROR_STOP=1 -U artifortress -d artifortress -c \
  "create table if not exists schema_migrations (version text primary key, applied_at timestamptz not null default now());"

shopt -s nullglob
for file in db/migrations/*.sql; do
  version="$(basename "$file")"
  applied="$(kubectl -n "$namespace" exec deploy/postgres -- psql -tAc "select 1 from schema_migrations where version = '${version}' limit 1;" -U artifortress -d artifortress | tr -d '[:space:]')"
  if [ "$applied" = "1" ]; then
    continue
  fi

  kubectl -n "$namespace" exec -i deploy/postgres -- psql -v ON_ERROR_STOP=1 -U artifortress -d artifortress < "$file"
  kubectl -n "$namespace" exec deploy/postgres -- psql -v ON_ERROR_STOP=1 -U artifortress -d artifortress -c \
    "insert into schema_migrations (version) values ('${version}');"
done

helm upgrade --install "$release_name" deploy/helm/artifortress \
  --namespace "$namespace" \
  --values deploy/helm/artifortress/values-kind-ha.yaml

kubectl -n "$namespace" rollout status deployment/artifortress-api --timeout=240s
kubectl -n "$namespace" rollout status deployment/artifortress-worker --timeout=240s

port_forward_log="$(mktemp)"
kubectl -n "$namespace" port-forward svc/artifortress-api "${api_port}:80" > "$port_forward_log" 2>&1 &
port_forward_pid="$!"
trap 'kill "$port_forward_pid" >/dev/null 2>&1 || true' EXIT
sleep 3

api_url="http://127.0.0.1:${api_port}"
curl -fsS "${api_url}/health/live" >/dev/null
curl -fsS "${api_url}/health/ready" >/dev/null

admin_token_response="$(curl -fsS \
  -X POST \
  "${api_url}/v1/auth/pats" \
  -H "Content-Type: application/json" \
  -H "X-Bootstrap-Token: ${bootstrap_token}" \
  --data '{"Subject":"kind-ha-admin","Scopes":["repo:*:admin"],"TtlMinutes":60}')"
admin_token="$(printf '%s' "$admin_token_response" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"

if [ -z "$admin_token" ]; then
  echo "Failed to issue admin PAT for kind HA validation." >&2
  exit 1
fi

API_URL="$api_url" \
ADMIN_TOKEN="$admin_token" \
ConnectionStrings__Postgres=redacted \
ObjectStorage__Endpoint=http://minio:9000 \
ObjectStorage__Bucket=artifortress-dev \
Auth__BootstrapToken=redacted \
KUBE_NAMESPACE="$namespace" \
HELM_RELEASE="$release_name" \
PREFLIGHT_REPORT_PATH=/tmp/artifortress-kind-production-preflight.md \
scripts/production-preflight.sh

kubectl -n "$namespace" rollout restart deployment/artifortress-api
kubectl -n "$namespace" rollout restart deployment/artifortress-worker
kubectl -n "$namespace" rollout status deployment/artifortress-api --timeout=240s
kubectl -n "$namespace" rollout status deployment/artifortress-worker --timeout=240s

kubectl -n "$namespace" scale deployment/artifortress-worker --replicas=0
kubectl -n "$namespace" wait --for=jsonpath='{.status.readyReplicas}'=0 deployment/artifortress-worker --timeout=120s || true
kubectl -n "$namespace" scale deployment/artifortress-worker --replicas=2
kubectl -n "$namespace" rollout status deployment/artifortress-worker --timeout=240s

kubectl -n "$namespace" scale deployment/artifortress-api --replicas=1
kubectl -n "$namespace" rollout status deployment/artifortress-api --timeout=240s
kubectl -n "$namespace" scale deployment/artifortress-api --replicas=3
kubectl -n "$namespace" rollout status deployment/artifortress-api --timeout=240s

pod_placement="$(kubectl -n "$namespace" get pods -o wide)"
api_replicas="$(kubectl -n "$namespace" get deployment artifortress-api -o jsonpath='{.status.readyReplicas}')"
worker_replicas="$(kubectl -n "$namespace" get deployment artifortress-worker -o jsonpath='{.status.readyReplicas}')"
kubernetes_version="$(kubectl version --short 2>/dev/null || kubectl version)"
helm_version="$(helm version --short)"
kind_version="$(kind version)"
ended_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat > "$report_path" <<EOF
# HA Kubernetes Validation Report

Generated at: ${ended_at}

## Summary

- overall status: PASS
- started at: ${started_at}
- ended at: ${ended_at}
- cluster: ${cluster_name}
- namespace: ${namespace}
- release: ${release_name}
- API ready replicas: ${api_replicas}
- worker ready replicas: ${worker_replicas}

## Tooling

\`\`\`text
${kind_version}
${helm_version}
${kubernetes_version}
\`\`\`

## Validated Scenarios

- kind cluster creation or reuse
- local API and worker image builds
- image load into kind nodes
- in-cluster Postgres and MinIO dependency startup
- MinIO bucket bootstrap
- SQL migrations applied through current head
- Helm install/upgrade
- API and worker rollout readiness
- API liveness/readiness over port-forward
- production preflight with Kubernetes and Helm checks
- API and worker rolling restart
- worker scale-down and restore
- API scale-down and restore

## Pod Placement

\`\`\`text
${pod_placement}
\`\`\`

## Production Preflight

- report: \`/tmp/artifortress-kind-production-preflight.md\`

## Residual Risks

- validation uses local kind infrastructure, not managed cloud Kubernetes
- Postgres and MinIO are single-replica validation dependencies
- ingress/TLS is not validated in the kind path
- production capacity is not certified by this validation
EOF

echo "HA Kubernetes validation report: ${report_path}"
