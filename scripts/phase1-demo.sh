#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8086}"
connection_string="${CONNECTION_STRING:-Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress}"
bootstrap_token="${BOOTSTRAP_TOKEN:-phase1-demo-bootstrap}"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"
log_file="/tmp/artifortress-phase1-demo.log"

if [ ! -f "$api_dll" ]; then
  echo "Missing API binary at $api_dll. Run 'make build' first."
  exit 1
fi

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

json_field() {
  local json="$1"
  local field="$2"
  printf '%s' "$json" | sed -n "s/.*\"$field\":\"\\([^\"]*\\)\".*/\\1/p"
}

contains_text() {
  local haystack="$1"
  local needle="$2"
  printf '%s' "$haystack" | grep -Fq "$needle"
}

echo "Starting API at $api_url"
ConnectionStrings__Postgres="$connection_string" Auth__BootstrapToken="$bootstrap_token" \
  dotnet "$api_dll" --urls "$api_url" >"$log_file" 2>&1 &
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
  cat "$log_file"
  exit 1
fi

suffix="$(date +%s)-$RANDOM"
repo_key="p1-demo-$suffix"
restricted_repo_key="p1-demo-denied-$suffix"
binding_subject="alice-$suffix"

echo "Step 1: issue admin PAT (bootstrap authorized)"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"admin-$suffix\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
admin_token="$(json_field "$response_body" "token")"
admin_token_id="$(json_field "$response_body" "tokenId")"

if [ -z "$admin_token" ] || [ -z "$admin_token_id" ]; then
  echo "Failed to parse admin token response: $response_body"
  exit 1
fi

echo "Step 2: create repository with admin token (expect 201)"
curl_call "POST" "/v1/repos" \
  "{\"RepoKey\":\"$repo_key\",\"RepoType\":\"local\",\"UpstreamUrl\":\"\",\"MemberRepos\":[]}" \
  -H "Authorization: Bearer $admin_token"
assert_status "201"

echo "Step 3: issue read-only PAT"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"reader-$suffix\",\"Scopes\":[\"repo:*:read\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
read_token="$(json_field "$response_body" "token")"

if [ -z "$read_token" ]; then
  echo "Failed to parse read token response: $response_body"
  exit 1
fi

echo "Step 4: verify forbidden repo create with read-only token (expect 403)"
curl_call "POST" "/v1/repos" \
  "{\"RepoKey\":\"$restricted_repo_key\",\"RepoType\":\"local\",\"UpstreamUrl\":\"\",\"MemberRepos\":[]}" \
  -H "Authorization: Bearer $read_token"
assert_status "403"

echo "Step 5: upsert role binding as admin (expect 200)"
curl_call "PUT" "/v1/repos/$repo_key/bindings/$binding_subject" \
  "{\"Roles\":[\"read\",\"write\"]}" \
  -H "Authorization: Bearer $admin_token"
assert_status "200"

echo "Step 6: issue subject PAT with empty scopes (derive from bindings)"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"$binding_subject\",\"Scopes\":[],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
derived_token="$(json_field "$response_body" "token")"

if [ -z "$derived_token" ]; then
  echo "Failed to parse derived token response: $response_body"
  exit 1
fi

echo "Step 7: verify derived token has expected scopes"
curl_call "GET" "/v1/auth/whoami" "" -H "Authorization: Bearer $derived_token"
assert_status "200"

if ! contains_text "$response_body" "repo:$repo_key:read"; then
  echo "Expected derived read scope for $repo_key."
  echo "whoami: $response_body"
  exit 1
fi

if ! contains_text "$response_body" "repo:$repo_key:write"; then
  echo "Expected derived write scope for $repo_key."
  echo "whoami: $response_body"
  exit 1
fi

echo "Step 8: revoke read token and verify unauthorized"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"revoked-$suffix\",\"Scopes\":[\"repo:*:read\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
revoked_token="$(json_field "$response_body" "token")"
revoked_token_id="$(json_field "$response_body" "tokenId")"

curl_call "POST" "/v1/auth/pats/revoke" \
  "{\"TokenId\":\"$revoked_token_id\"}" \
  -H "Authorization: Bearer $admin_token"
assert_status "200"

curl_call "GET" "/v1/auth/whoami" "" -H "Authorization: Bearer $revoked_token"
assert_status "401"

echo "Step 9: verify privileged audit events are persisted"
curl_call "GET" "/v1/audit?limit=200" "" -H "Authorization: Bearer $admin_token"
assert_status "200"

if ! contains_text "$response_body" "auth.pat.issued"; then
  echo "Audit response missing auth.pat.issued event."
  exit 1
fi

if ! contains_text "$response_body" "repo.created"; then
  echo "Audit response missing repo.created event."
  exit 1
fi

if ! contains_text "$response_body" "repo.binding.upserted"; then
  echo "Audit response missing repo.binding.upserted event."
  exit 1
fi

if ! contains_text "$response_body" "auth.pat.revoked"; then
  echo "Audit response missing auth.pat.revoked event."
  exit 1
fi

echo "Phase 1 demo completed successfully."
