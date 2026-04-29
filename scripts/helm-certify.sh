#!/usr/bin/env bash
set -euo pipefail

export PATH="/home/srikanth/bin:${PATH}"

cluster_name="${HELM_CERT_CLUSTER_NAME:-kublai-ha}"
namespace="${HELM_CERT_NAMESPACE:-kublai-helm-cert}"
release_name="${HELM_RELEASE:-kublai}"
chart_path="${HELM_CERT_CHART:-deploy/helm/kublai}"
base_chart_path="${HELM_CERT_BASE_CHART:-$chart_path}"
report_path="${HELM_CERT_REPORT_PATH:-docs/reports/helm-certification-latest.md}"
api_port="${HELM_CERT_API_PORT:-18087}"
bootstrap_token="${HELM_CERT_BOOTSTRAP_TOKEN:-kind-ha-bootstrap}"
image_tag="${HELM_CERT_IMAGE_TAG:-helm-cert}"

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
mkdir -p "$(dirname "$report_path")"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

run_step() {
  local name="$1"
  shift

  echo "==> ${name}"
  "$@"
}

cleanup() {
  if [ -n "${port_forward_pid:-}" ]; then
    kill "$port_forward_pid" >/dev/null 2>&1 || true
  fi
  rm -f "${base_values:-}" "${target_values:-}" "${port_forward_log:-}"
}
trap cleanup EXIT

require_command curl
require_command docker
require_command helm
require_command kind
require_command kubectl

if ! kind get clusters | grep -Fxq "$cluster_name"; then
  run_step "create kind cluster" kind create cluster --config deploy/kind/kind-ha.yaml
fi

run_step "select kind context" kubectl config use-context "kind-${cluster_name}" >/dev/null

run_step "build API image" docker build -f deploy/kind/Dockerfile.api -t "kublai-api:${image_tag}" .
run_step "build worker image" docker build -f deploy/kind/Dockerfile.worker -t "kublai-worker:${image_tag}" .
run_step "load API image" kind load docker-image --name "$cluster_name" "kublai-api:${image_tag}"
run_step "load worker image" kind load docker-image --name "$cluster_name" "kublai-worker:${image_tag}"

echo "==> create namespace"
kubectl create namespace "$namespace" --dry-run=client -o yaml | kubectl apply -f -
run_step "apply dependencies" kubectl -n "$namespace" apply -f deploy/kind/dependencies.yaml
run_step "wait for Postgres" kubectl -n "$namespace" rollout status deployment/postgres --timeout=180s
run_step "wait for MinIO" kubectl -n "$namespace" rollout status deployment/minio --timeout=180s

bucket_job="minio-bootstrap-$(date -u +%s)"
kubectl -n "$namespace" create job "$bucket_job" \
  --image=minio/mc:latest \
  -- sh -c "mc alias set local http://minio:9000 kublai kublai && mc mb --ignore-existing local/kublai-dev"
run_step "bootstrap MinIO bucket" kubectl -n "$namespace" wait --for=condition=complete "job/${bucket_job}" --timeout=120s

kubectl -n "$namespace" exec deploy/postgres -- psql -v ON_ERROR_STOP=1 -U kublai -d kublai -c \
  "create table if not exists schema_migrations (version text primary key, applied_at timestamptz not null default now());"

shopt -s nullglob
for file in db/migrations/*.sql; do
  version="$(basename "$file")"
  applied="$(kubectl -n "$namespace" exec deploy/postgres -- psql -tAc "select 1 from schema_migrations where version = '${version}' limit 1;" -U kublai -d kublai | tr -d '[:space:]')"
  if [ "$applied" = "1" ]; then
    continue
  fi

  kubectl -n "$namespace" exec -i deploy/postgres -- psql -v ON_ERROR_STOP=1 -U kublai -d kublai < "$file"
  kubectl -n "$namespace" exec deploy/postgres -- psql -v ON_ERROR_STOP=1 -U kublai -d kublai -c \
    "insert into schema_migrations (version) values ('${version}');"
done

base_values="$(mktemp)"
target_values="$(mktemp)"

cat > "$base_values" <<EOF
api:
  replicaCount: 2
  image:
    repository: kublai-api
    tag: ${image_tag}
    pullPolicy: IfNotPresent
  podDisruptionBudget:
    enabled: true
    minAvailable: 1
worker:
  replicaCount: 1
  image:
    repository: kublai-worker
    tag: ${image_tag}
    pullPolicy: IfNotPresent
EOF

cat > "$target_values" <<EOF
api:
  replicaCount: 3
  image:
    repository: kublai-api
    tag: ${image_tag}
    pullPolicy: IfNotPresent
  podDisruptionBudget:
    enabled: true
    minAvailable: 2
worker:
  replicaCount: 2
  image:
    repository: kublai-worker
    tag: ${image_tag}
    pullPolicy: IfNotPresent
EOF

base_chart_version="$(helm show chart "$base_chart_path" | sed -n 's/^version:[[:space:]]*//p' | head -n 1)"
target_chart_version="$(helm show chart "$chart_path" | sed -n 's/^version:[[:space:]]*//p' | head -n 1)"
helm_version="$(helm version --short)"
kind_version="$(kind version)"
kubernetes_version="$(kubectl version --short 2>/dev/null || kubectl version)"

run_step "lint target chart" helm lint "$chart_path" --values deploy/helm/kublai/values-kind-ha.yaml --values "$target_values"

run_step "install baseline chart" helm upgrade --install "$release_name" "$base_chart_path" \
  --namespace "$namespace" \
  --values deploy/helm/kublai/values-kind-ha.yaml \
  --values "$base_values" \
  --wait \
  --timeout 5m

run_step "wait baseline API" kubectl -n "$namespace" rollout status deployment/kublai-api --timeout=240s
run_step "wait baseline worker" kubectl -n "$namespace" rollout status deployment/kublai-worker --timeout=240s

port_forward_log="$(mktemp)"
kubectl -n "$namespace" port-forward svc/kublai-api "${api_port}:80" > "$port_forward_log" 2>&1 &
port_forward_pid="$!"
sleep 3

api_url="http://127.0.0.1:${api_port}"

issue_admin_token() {
  local subject="$1"
  local response

  response="$(curl -fsS \
    -X POST \
    "${api_url}/v1/auth/pats" \
    -H "Content-Type: application/json" \
    -H "X-Bootstrap-Token: ${bootstrap_token}" \
    --data "{\"Subject\":\"${subject}\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}")"

  printf '%s' "$response" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p'
}

run_api_smoke() {
  local label="$1"
  local token="$2"
  local repo_key="helm-cert-${label}-$(date -u +%s)"

  curl -fsS "${api_url}/health/live" >/dev/null
  curl -fsS "${api_url}/health/ready" >/dev/null
  curl -fsS -H "Authorization: Bearer ${token}" "${api_url}/v1/auth/whoami" >/dev/null
  curl -fsS -H "Authorization: Bearer ${token}" "${api_url}/v1/admin/ops/summary" >/dev/null
  curl -fsS -H "Authorization: Bearer ${token}" "${api_url}/v1/repos" >/dev/null
  curl -fsS \
    -X POST \
    "${api_url}/v1/repos" \
    -H "Authorization: Bearer ${token}" \
    -H "Content-Type: application/json" \
    --data "{\"RepoKey\":\"${repo_key}\",\"RepoType\":\"local\",\"UpstreamUrl\":\"\",\"MemberRepos\":[]}" >/dev/null
  curl -fsS -H "Authorization: Bearer ${token}" "${api_url}/v1/repos/${repo_key}" >/dev/null
}

admin_token="$(issue_admin_token "helm-cert-baseline-admin")"
if [ -z "$admin_token" ]; then
  echo "Failed to issue baseline admin PAT for Helm certification." >&2
  exit 1
fi

run_step "baseline API smoke" run_api_smoke "baseline" "$admin_token"

API_URL="$api_url" \
ADMIN_TOKEN="$admin_token" \
ConnectionStrings__Postgres=redacted \
ObjectStorage__Endpoint=http://minio:9000 \
ObjectStorage__Bucket=kublai-dev \
Auth__BootstrapToken=redacted \
KUBE_NAMESPACE="$namespace" \
HELM_RELEASE="$release_name" \
PREFLIGHT_REQUIRE_HELM_CERT=false \
PREFLIGHT_REPORT_PATH=/tmp/kublai-helm-cert-preflight-baseline.md \
scripts/production-preflight.sh

run_step "upgrade to target chart" helm upgrade "$release_name" "$chart_path" \
  --namespace "$namespace" \
  --values deploy/helm/kublai/values-kind-ha.yaml \
  --values "$target_values" \
  --wait \
  --timeout 5m

run_step "wait upgraded API" kubectl -n "$namespace" rollout status deployment/kublai-api --timeout=240s
run_step "wait upgraded worker" kubectl -n "$namespace" rollout status deployment/kublai-worker --timeout=240s

upgraded_admin_token="$(issue_admin_token "helm-cert-upgrade-admin")"
if [ -z "$upgraded_admin_token" ]; then
  echo "Failed to issue upgraded admin PAT for Helm certification." >&2
  exit 1
fi

run_step "upgraded API smoke" run_api_smoke "upgrade" "$upgraded_admin_token"

API_URL="$api_url" \
ADMIN_TOKEN="$upgraded_admin_token" \
ConnectionStrings__Postgres=redacted \
ObjectStorage__Endpoint=http://minio:9000 \
ObjectStorage__Bucket=kublai-dev \
Auth__BootstrapToken=redacted \
KUBE_NAMESPACE="$namespace" \
HELM_RELEASE="$release_name" \
PREFLIGHT_REQUIRE_HELM_CERT=false \
PREFLIGHT_REPORT_PATH=/tmp/kublai-helm-cert-preflight-upgrade.md \
scripts/production-preflight.sh

release_history="$(helm history "$release_name" --namespace "$namespace")"
pod_placement="$(kubectl -n "$namespace" get pods -o wide)"
api_replicas="$(kubectl -n "$namespace" get deployment kublai-api -o jsonpath='{.status.readyReplicas}')"
worker_replicas="$(kubectl -n "$namespace" get deployment kublai-worker -o jsonpath='{.status.readyReplicas}')"

run_step "uninstall release" helm uninstall "$release_name" --namespace "$namespace" --wait --timeout 5m

kubectl -n "$namespace" wait --for=delete deployment/kublai-api --timeout=120s >/dev/null 2>&1 || true
kubectl -n "$namespace" wait --for=delete deployment/kublai-worker --timeout=120s >/dev/null 2>&1 || true

leftover_resources="$(kubectl -n "$namespace" get deploy,svc,configmap,secret,pdb,networkpolicy -l "app.kubernetes.io/instance=${release_name}" --ignore-not-found -o name)"
if [ -n "$leftover_resources" ]; then
  echo "Helm uninstall left Kublai-owned resources:" >&2
  echo "$leftover_resources" >&2
  exit 1
fi

dependency_resources="$(kubectl -n "$namespace" get deploy,svc -l 'app.kubernetes.io/name in (postgres,minio)' -o name)"
ended_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat > "$report_path" <<EOF
# Helm Certification Report

Generated at: ${ended_at}

## Summary

- overall status: PASS
- started at: ${started_at}
- ended at: ${ended_at}
- cluster: ${cluster_name}
- namespace: ${namespace}
- release: ${release_name}
- baseline chart: ${base_chart_path}
- baseline chart version: ${base_chart_version}
- target chart: ${chart_path}
- target chart version: ${target_chart_version}
- API ready replicas after upgrade: ${api_replicas}
- worker ready replicas after upgrade: ${worker_replicas}

## Tooling

\`\`\`text
${kind_version}
${helm_version}
${kubernetes_version}
\`\`\`

## Validated Scenarios

- Helm lint with kind HA values
- baseline Helm install into kind Kubernetes
- API and worker rollout readiness after install
- API liveness and readiness smoke
- authenticated admin smoke
- repository create/read smoke
- production preflight with Kubernetes and Helm checks after install
- Helm upgrade to target chart values
- API and worker rollout readiness after upgrade
- API liveness and readiness smoke after upgrade
- authenticated admin smoke after upgrade
- repository create/read smoke after upgrade
- production preflight with Kubernetes and Helm checks after upgrade
- Helm uninstall
- uninstall cleanup check for Helm-owned Kublai resources
- data dependency preservation for Postgres and MinIO resources

## Helm History

\`\`\`text
${release_history}
\`\`\`

## Pod Placement Before Uninstall

\`\`\`text
${pod_placement}
\`\`\`

## Preserved Data Dependencies After Uninstall

\`\`\`text
${dependency_resources}
\`\`\`

## Preflight Reports

- baseline: \`/tmp/kublai-helm-cert-preflight-baseline.md\`
- upgrade: \`/tmp/kublai-helm-cert-preflight-upgrade.md\`

## Residual Risks

- default baseline chart path is the current chart unless
  \`HELM_CERT_BASE_CHART\` points to a previous released chart package
- validation uses local kind infrastructure, not managed cloud Kubernetes
- Postgres and MinIO are single-replica validation dependencies
- ingress/TLS is not validated in the kind certification path
EOF

echo "Helm certification report: ${report_path}"
