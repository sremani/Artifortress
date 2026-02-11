#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8086}"
connection_string="${CONNECTION_STRING:-Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress}"
bootstrap_token="${BOOTSTRAP_TOKEN:-phase4-demo-bootstrap}"
db_user="${POSTGRES_USER:-artifortress}"
db_name="${POSTGRES_DB:-artifortress}"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"
worker_dll="src/Artifortress.Worker/bin/Debug/net10.0/Artifortress.Worker.dll"
api_log_file="/tmp/artifortress-phase4-demo-api.log"
worker_log_file="/tmp/artifortress-phase4-demo-worker.log"

if [ ! -f "$api_dll" ]; then
  echo "Missing API binary at $api_dll. Run 'make build' first."
  exit 1
fi

if [ ! -f "$worker_dll" ]; then
  echo "Missing worker binary at $worker_dll. Run 'make build' first."
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

psql_exec() {
  docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d "$db_name" "$@"
}

sql_scalar() {
  local query="$1"
  psql_exec -tAc "$query" | tr -d '\n' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

sql_exec() {
  local query="$1"
  psql_exec -c "$query" >/dev/null
}

reset_search_pipeline_global() {
  sql_exec "delete from search_index_jobs;"
  sql_exec "delete from outbox_events where event_type = 'version.published';"
}

mark_version_published() {
  local version_id="$1"
  sql_exec "update package_versions set state = 'published', published_at = now() where version_id = '$version_id';"
}

insert_version_published_event() {
  local version_id="$1"
  local payload="{\"versionId\":\"$version_id\"}"
  sql_exec "insert into outbox_events (tenant_id, aggregate_type, aggregate_id, event_type, payload, available_at, occurred_at) values ((select tenant_id from tenants where slug = 'default' limit 1), 'package_version', '$version_id', 'version.published', '$payload'::jsonb, now(), now());"
}

insert_malformed_version_published_event() {
  sql_scalar "insert into outbox_events (tenant_id, aggregate_type, aggregate_id, event_type, payload, available_at, occurred_at) values ((select tenant_id from tenants where slug = 'default' limit 1), 'package_version', 'not-a-guid', 'version.published', '{}'::jsonb, now(), now()) returning event_id;"
}

run_worker_once() {
  ConnectionStrings__Postgres="$connection_string" dotnet "$worker_dll" --once >>"$worker_log_file" 2>&1
}

echo "Starting API at $api_url"
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

suffix="$(date +%s)-$RANDOM"
repo_key="p4-demo-$suffix"

echo "Step 1: issue admin PAT and create demo repository"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p4-admin-$suffix\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
admin_token="$(json_string_field "$response_body" "token")"

if [ -z "$admin_token" ]; then
  echo "Failed to parse admin token: $response_body"
  exit 1
fi

curl_call "POST" "/v1/repos" \
  "{\"RepoKey\":\"$repo_key\",\"RepoType\":\"local\",\"UpstreamUrl\":\"\",\"MemberRepos\":[]}" \
  -H "Authorization: Bearer $admin_token"
assert_status "201"

echo "Step 2: issue write/promote PATs"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p4-writer-$suffix\",\"Scopes\":[\"repo:$repo_key:write\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
write_token="$(json_string_field "$response_body" "token")"

curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p4-promote-$suffix\",\"Scopes\":[\"repo:$repo_key:promote\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
promote_token="$(json_string_field "$response_body" "token")"

if [ -z "$write_token" ] || [ -z "$promote_token" ]; then
  echo "Failed to parse write/promote token responses."
  exit 1
fi

echo "Step 3: policy deny path (deterministic, no quarantine side effect)"
deny_package="deny-$suffix"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/drafts" \
  "{\"PackageType\":\"nuget\",\"PackageNamespace\":\"\",\"PackageName\":\"$deny_package\",\"Version\":\"1.0.0\"}" \
  -H "Authorization: Bearer $write_token"
assert_status "201"
deny_version_id="$(json_string_field "$response_body" "versionId")"

curl_call "POST" "/v1/repos/$repo_key/policy/evaluations" \
  "{\"Action\":\"promote\",\"VersionId\":\"$deny_version_id\",\"DecisionHint\":\"deny\",\"Reason\":\"phase4 demo deny\",\"PolicyEngineVersion\":\"phase4-demo-v1\"}" \
  -H "Authorization: Bearer $promote_token"
assert_status "201"

if ! contains_text "$response_body" "\"decision\":\"deny\""; then
  echo "Expected deny decision response. Body: $response_body"
  exit 1
fi

if contains_text "$response_body" "\"quarantined\":true"; then
  echo "Deny path unexpectedly marked quarantined. Body: $response_body"
  exit 1
fi

echo "Step 4: quarantine + release flow"
quarantine_package="quarantine-$suffix"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/drafts" \
  "{\"PackageType\":\"nuget\",\"PackageNamespace\":\"\",\"PackageName\":\"$quarantine_package\",\"Version\":\"1.0.1\"}" \
  -H "Authorization: Bearer $write_token"
assert_status "201"
quarantine_version_id="$(json_string_field "$response_body" "versionId")"

curl_call "POST" "/v1/repos/$repo_key/policy/evaluations" \
  "{\"Action\":\"promote\",\"VersionId\":\"$quarantine_version_id\",\"DecisionHint\":\"quarantine\",\"Reason\":\"phase4 demo quarantine\",\"PolicyEngineVersion\":\"phase4-demo-v1\"}" \
  -H "Authorization: Bearer $promote_token"
assert_status "201"
quarantine_id="$(json_string_field "$response_body" "quarantineId")"

if [ -z "$quarantine_id" ]; then
  echo "Expected quarantineId in quarantine response. Body: $response_body"
  exit 1
fi

curl_call "GET" "/v1/repos/$repo_key/quarantine?status=quarantined" "" \
  -H "Authorization: Bearer $promote_token"
assert_status "200"

if ! contains_text "$response_body" "$quarantine_id"; then
  echo "Expected quarantine list to include quarantine id $quarantine_id."
  echo "Body: $response_body"
  exit 1
fi

curl_call "POST" "/v1/repos/$repo_key/quarantine/$quarantine_id/release" "" \
  -H "Authorization: Bearer $promote_token"
assert_status "200"

echo "Step 5: reset search pipeline state for deterministic checks"
reset_search_pipeline_global

echo "Step 6: search pipeline happy path (published version -> completed search job)"
published_package="search-published-$suffix"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/drafts" \
  "{\"PackageType\":\"nuget\",\"PackageNamespace\":\"\",\"PackageName\":\"$published_package\",\"Version\":\"2.0.0\"}" \
  -H "Authorization: Bearer $write_token"
assert_status "201"
published_version_id="$(json_string_field "$response_body" "versionId")"

mark_version_published "$published_version_id"
insert_version_published_event "$published_version_id"
run_worker_once

published_status="$(sql_scalar "select status from search_index_jobs where version_id = '$published_version_id' limit 1;")"
if [ "$published_status" != "completed" ]; then
  echo "Expected completed search job for published version, got '$published_status'."
  echo "Worker logs:"
  cat "$worker_log_file"
  exit 1
fi

echo "Step 7: degraded search path (unpublished version -> failed job)"
unpublished_package="search-unpublished-$suffix"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/drafts" \
  "{\"PackageType\":\"nuget\",\"PackageNamespace\":\"\",\"PackageName\":\"$unpublished_package\",\"Version\":\"2.0.1\"}" \
  -H "Authorization: Bearer $write_token"
assert_status "201"
unpublished_version_id="$(json_string_field "$response_body" "versionId")"

insert_version_published_event "$unpublished_version_id"
run_worker_once

unpublished_status="$(sql_scalar "select status from search_index_jobs where version_id = '$unpublished_version_id' limit 1;")"
unpublished_error="$(sql_scalar "select coalesce(last_error, '') from search_index_jobs where version_id = '$unpublished_version_id' limit 1;")"

if [ "$unpublished_status" != "failed" ] || [ "$unpublished_error" != "version_not_published" ]; then
  echo "Expected failed search job with version_not_published, got status='$unpublished_status' error='$unpublished_error'."
  echo "Worker logs:"
  cat "$worker_log_file"
  exit 1
fi

echo "Step 8: malformed outbox event is requeued (not delivered)"
malformed_event_id="$(insert_malformed_version_published_event)"
run_worker_once
malformed_delivered="$(sql_scalar "select case when delivered_at is null then 'false' else 'true' end from outbox_events where event_id = '$malformed_event_id' limit 1;")"

if [ "$malformed_delivered" != "false" ]; then
  echo "Expected malformed outbox event to remain undelivered."
  exit 1
fi

echo "Step 9: quarantine release remains correct while degraded search artifacts exist"
followup_package="quarantine-followup-$suffix"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/drafts" \
  "{\"PackageType\":\"nuget\",\"PackageNamespace\":\"\",\"PackageName\":\"$followup_package\",\"Version\":\"3.0.0\"}" \
  -H "Authorization: Bearer $write_token"
assert_status "201"
followup_version_id="$(json_string_field "$response_body" "versionId")"

curl_call "POST" "/v1/repos/$repo_key/policy/evaluations" \
  "{\"Action\":\"promote\",\"VersionId\":\"$followup_version_id\",\"DecisionHint\":\"quarantine\",\"Reason\":\"phase4 demo followup quarantine\",\"PolicyEngineVersion\":\"phase4-demo-v1\"}" \
  -H "Authorization: Bearer $promote_token"
assert_status "201"
followup_quarantine_id="$(json_string_field "$response_body" "quarantineId")"

curl_call "POST" "/v1/repos/$repo_key/quarantine/$followup_quarantine_id/release" "" \
  -H "Authorization: Bearer $promote_token"
assert_status "200"

curl_call "GET" "/v1/repos/$repo_key/quarantine?status=released" "" \
  -H "Authorization: Bearer $promote_token"
assert_status "200"

if ! contains_text "$response_body" "$followup_quarantine_id"; then
  echo "Expected released quarantine list to include id $followup_quarantine_id."
  echo "Body: $response_body"
  exit 1
fi

echo "Step 10: audit includes policy and quarantine actions"
curl_call "GET" "/v1/audit?limit=300" "" -H "Authorization: Bearer $admin_token"
assert_status "200"

if ! contains_text "$response_body" "policy.evaluated"; then
  echo "Expected policy.evaluated in audit response."
  exit 1
fi

if ! contains_text "$response_body" "quarantine.released"; then
  echo "Expected quarantine.released in audit response."
  exit 1
fi

echo "Phase 4 demo completed successfully."
