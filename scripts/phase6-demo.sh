#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8086}"
connection_string="${CONNECTION_STRING:-Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress}"
bootstrap_token="${BOOTSTRAP_TOKEN:-phase6-demo-bootstrap}"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"
api_log_file="/tmp/artifortress-phase6-demo-api.log"
report_path="${REPORT_PATH:-docs/reports/phase6-ga-readiness-latest.md}"

if [ ! -f "$api_dll" ]; then
  echo "Missing API binary at $api_dll. Run 'make build' first."
  exit 1
fi

response_status=""
response_body=""

curl_call() {
  local method="$1"
  local path="$2"
  local data="$3"
  shift 3

  local temp_body
  temp_body="$(mktemp)"

  if [ -n "$data" ]; then
    response_status="$(curl -sS -o "$temp_body" -w "%{http_code}" -X "$method" "$api_url$path" -H "Content-Type: application/json" "$@" --data "$data")"
  else
    response_status="$(curl -sS -o "$temp_body" -w "%{http_code}" -X "$method" "$api_url$path" "$@")"
  fi

  response_body="$(cat "$temp_body")"
  rm -f "$temp_body"
}

assert_status() {
  local expected="$1"
  if [ "$response_status" != "$expected" ]; then
    echo "Expected HTTP $expected but got $response_status."
    echo "Response body: $response_body"
    exit 1
  fi
}

json_string_field() {
  local json="$1"
  local field="$2"
  printf '%s' "$json" | sed -n "s/.*\"$field\":\"\\([^\"]*\\)\".*/\\1/p"
}

contains_text() {
  local haystack="$1"
  local needle="$2"
  printf '%s' "$haystack" | grep -Fq "$needle"
}

echo "[phase6-demo] Running static checks..."
make test
make format

echo "[phase6-demo] Running RPO/RTO drill..."
./scripts/phase6-drill.sh

echo "[phase6-demo] Starting API at $api_url"
ConnectionStrings__Postgres="$connection_string" Auth__BootstrapToken="$bootstrap_token" \
  dotnet "$api_dll" --urls "$api_url" >"$api_log_file" 2>&1 &
api_pid=$!

cleanup() {
  kill "$api_pid" >/dev/null 2>&1 || true
}
trap cleanup EXIT

for _ in {1..40}; do
  if curl -sf "$api_url/health/live" >/dev/null; then
    break
  fi
  sleep 1
done

if ! curl -sf "$api_url/health/live" >/dev/null; then
  echo "API did not become healthy."
  echo "Logs:"
  cat "$api_log_file"
  exit 1
fi

echo "[phase6-demo] Checking readiness endpoint"
curl_call "GET" "/health/ready" ""
assert_status "200"
if ! contains_text "$response_body" "\"status\":\"ready\""; then
  echo "Readiness endpoint did not return ready. Body: $response_body"
  exit 1
fi

echo "[phase6-demo] Issuing admin token and querying ops summary"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"phase6-admin\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
admin_token="$(json_string_field "$response_body" "token")"

if [ -z "$admin_token" ]; then
  echo "Failed to parse admin token from response."
  exit 1
fi

curl_call "GET" "/v1/admin/ops/summary" "" -H "Authorization: Bearer $admin_token"
assert_status "200"
ops_summary_body="$response_body"

if ! contains_text "$ops_summary_body" "pendingOutboxEvents"; then
  echo "ops summary payload missing expected fields. Body: $ops_summary_body"
  exit 1
fi

mkdir -p "$(dirname "$report_path")"
{
  echo "# Phase 6 GA Readiness Report"
  echo
  echo "Generated at: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "## Checks"
  echo
  echo "- make test: PASS"
  echo "- make format: PASS"
  echo "- phase6-drill: PASS"
  echo "- /health/ready: PASS"
  echo "- /v1/admin/ops/summary: PASS"
  echo
  echo "## Ops Summary Snapshot"
  echo
  echo '```json'
  echo "$ops_summary_body"
  echo '```'
} > "$report_path"

echo "[phase6-demo] Report written to $report_path"
echo "[phase6-demo] Phase 6 GA readiness demo completed successfully."
