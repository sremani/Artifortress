#!/usr/bin/env bash
set -u -o pipefail

REPORT_PATH="${PREFLIGHT_REPORT_PATH:-docs/reports/production-preflight-latest.md}"
CURL_TIMEOUT_SECONDS="${PREFLIGHT_CURL_TIMEOUT_SECONDS:-10}"
OFFLINE_MODE="${PREFLIGHT_OFFLINE:-false}"

mkdir -p "$(dirname "$REPORT_PATH")"

pass_count=0
warn_count=0
fail_count=0

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat > "$REPORT_PATH" <<EOF
# Production Preflight Report

Generated at: ${started_at}

## Results

| Check | Status | Detail |
|---|---|---|
EOF

record() {
  local name="$1"
  local status="$2"
  local detail="$3"

  case "$status" in
    PASS) pass_count=$((pass_count + 1)) ;;
    WARN) warn_count=$((warn_count + 1)) ;;
    FAIL) fail_count=$((fail_count + 1)) ;;
  esac

  detail="${detail//|/\\|}"
  printf '| %s | %s | %s |\n' "$name" "$status" "$detail" >> "$REPORT_PATH"
}

require_file() {
  local path="$1"
  if [ -f "$path" ]; then
    record "$path" PASS "file exists"
  else
    record "$path" FAIL "required file is missing"
  fi
}

require_dir() {
  local path="$1"
  if [ -d "$path" ]; then
    record "$path" PASS "directory exists"
  else
    record "$path" FAIL "required directory is missing"
  fi
}

require_env() {
  local key="$1"
  if [ -n "${!key+x}" ] && [ -n "${!key}" ]; then
    record "$key" PASS "configured"
  else
    record "$key" FAIL "required environment variable is missing"
  fi
}

optional_env() {
  local key="$1"
  if [ -n "${!key+x}" ] && [ -n "${!key}" ]; then
    record "$key" PASS "configured"
  else
    record "$key" WARN "not configured"
  fi
}

has_command() {
  command -v "$1" >/dev/null 2>&1
}

http_get() {
  local url="$1"
  local output_path="$2"
  shift 2

  curl -sS --max-time "$CURL_TIMEOUT_SECONDS" "$@" "$url" > "$output_path" 2>&1
}

check_json_status_ready() {
  local path="$1"
  if has_command jq; then
    jq -e '.status == "ready"' "$path" >/dev/null 2>&1
  else
    grep -Eq '"status"[[:space:]]*:[[:space:]]*"ready"' "$path"
  fi
}

require_file README.md
require_file Makefile
require_file docker-compose.yml
require_file deploy/enterprise-ops-alert-thresholds.yaml
require_file deploy/grafana-artifortress-operations-dashboard.json
require_dir deploy/helm/artifortress
require_dir deploy/kubernetes/production
require_file docs/59-enterprise-product-envelope.md
require_file docs/60-versioning-support-policy.md
require_file docs/61-release-candidate-signoff.md
require_file docs/68-slo-sli-alerting.md
require_file docs/69-ha-kubernetes-validation-plan.md
require_file docs/70-production-preflight.md

require_file docs/reports/enterprise-verification-latest.md
require_file docs/reports/phase6-rto-rpo-drill-latest.md
require_file docs/reports/upgrade-compatibility-drill-latest.md
require_file docs/reports/reliability-drill-latest.md
if [ "${PREFLIGHT_REQUIRE_HELM_CERT:-true}" = "true" ]; then
  require_file docs/reports/helm-certification-latest.md
else
  record "docs/reports/helm-certification-latest.md" WARN "skipped because PREFLIGHT_REQUIRE_HELM_CERT=false"
fi

require_env API_URL
require_env ADMIN_TOKEN
require_env ConnectionStrings__Postgres
require_env ObjectStorage__Endpoint
require_env ObjectStorage__Bucket
require_env Auth__BootstrapToken
optional_env ASPNETCORE_ENVIRONMENT
optional_env ObjectStorage__AccessKey
optional_env ObjectStorage__SecretKey

if [ "${Auth__Oidc__Enabled:-false}" = "true" ]; then
  require_env Auth__Oidc__Issuer
  require_env Auth__Oidc__Audience
  if [ -n "${Auth__Oidc__Hs256SharedSecret:-}" ] || [ -n "${Auth__Oidc__JwksJson:-}" ] || [ -n "${Auth__Oidc__JwksUrl:-}" ]; then
    record "OIDC signing material" PASS "at least one signing mode configured"
  else
    record "OIDC signing material" FAIL "OIDC enabled without signing material"
  fi
else
  record "OIDC" WARN "not enabled in preflight environment"
fi

if [ "${Auth__Saml__Enabled:-false}" = "true" ]; then
  require_env Auth__Saml__ExpectedIssuer
  require_env Auth__Saml__ServiceProviderEntityId
  if [ -n "${Auth__Saml__IdpMetadataUrl:-}" ] || [ -n "${Auth__Saml__IdpMetadataXml:-}" ]; then
    record "SAML metadata" PASS "metadata source configured"
  else
    record "SAML metadata" FAIL "SAML enabled without metadata URL or static XML"
  fi
else
  record "SAML" WARN "not enabled in preflight environment"
fi

if [ "$OFFLINE_MODE" = "true" ]; then
  record "online API checks" WARN "skipped because PREFLIGHT_OFFLINE=true"
else
  if [ -n "${API_URL:-}" ]; then
    live_output="$(mktemp)"
    ready_output="$(mktemp)"
    ops_output="$(mktemp)"

    if http_get "${API_URL%/}/health/live" "$live_output"; then
      record "/health/live" PASS "endpoint responded"
    else
      record "/health/live" FAIL "endpoint did not respond"
    fi

    if http_get "${API_URL%/}/health/ready" "$ready_output"; then
      if check_json_status_ready "$ready_output"; then
        record "/health/ready" PASS "status is ready"
      else
        record "/health/ready" FAIL "endpoint responded but status is not ready"
      fi
    else
      record "/health/ready" FAIL "endpoint did not respond"
    fi

    if [ -n "${ADMIN_TOKEN:-}" ]; then
      if http_get "${API_URL%/}/v1/admin/ops/summary" "$ops_output" -H "Authorization: Bearer ${ADMIN_TOKEN}"; then
        record "/v1/admin/ops/summary" PASS "endpoint responded"
      else
        record "/v1/admin/ops/summary" FAIL "endpoint did not respond with provided admin token"
      fi
    else
      record "/v1/admin/ops/summary" FAIL "ADMIN_TOKEN required for ops summary"
    fi

    rm -f "$live_output" "$ready_output" "$ops_output"
  fi
fi

if [[ "${API_URL:-}" == https://* ]]; then
  record "ingress TLS" PASS "API_URL uses HTTPS"
else
  record "ingress TLS" WARN "API_URL is not HTTPS or is unset"
fi

if has_command kubectl && [ -n "${KUBE_NAMESPACE:-}" ]; then
  if kubectl -n "$KUBE_NAMESPACE" get deployment artifortress-api >/dev/null 2>&1; then
    record "kubernetes api deployment" PASS "artifortress-api deployment exists"
  else
    record "kubernetes api deployment" FAIL "artifortress-api deployment missing"
  fi

  if kubectl -n "$KUBE_NAMESPACE" get deployment artifortress-worker >/dev/null 2>&1; then
    record "kubernetes worker deployment" PASS "artifortress-worker deployment exists"
  else
    record "kubernetes worker deployment" FAIL "artifortress-worker deployment missing"
  fi
else
  record "kubernetes deployment checks" WARN "kubectl or KUBE_NAMESPACE unavailable"
fi

if has_command helm && [ -n "${HELM_RELEASE:-}" ] && [ -n "${KUBE_NAMESPACE:-}" ]; then
  if helm status "$HELM_RELEASE" -n "$KUBE_NAMESPACE" >/dev/null 2>&1; then
    record "helm release" PASS "release status is available"
  else
    record "helm release" FAIL "release status not available"
  fi
else
  record "helm release check" WARN "helm, HELM_RELEASE, or KUBE_NAMESPACE unavailable"
fi

ended_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat >> "$REPORT_PATH" <<EOF

## Summary

- overall status: $([ "$fail_count" -eq 0 ] && echo PASS || echo FAIL)
- started at: ${started_at}
- ended at: ${ended_at}
- pass count: ${pass_count}
- warning count: ${warn_count}
- failure count: ${fail_count}
EOF

echo "Production preflight report: ${REPORT_PATH}"

if [ "$fail_count" -eq 0 ]; then
  exit 0
fi

exit 1
