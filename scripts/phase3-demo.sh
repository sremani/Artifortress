#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8086}"
connection_string="${CONNECTION_STRING:-Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress}"
bootstrap_token="${BOOTSTRAP_TOKEN:-phase3-demo-bootstrap}"
db_user="${POSTGRES_USER:-artifortress}"
db_name="${POSTGRES_DB:-artifortress}"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"
api_log_file="/tmp/artifortress-phase3-demo-api.log"

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

psql_exec() {
  docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d "$db_name" "$@"
}

sql_exec() {
  local query="$1"
  psql_exec -c "$query" >/dev/null
}

sql_scalar() {
  local query="$1"
  psql_exec -tAc "$query" | tr -d '\n' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
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
repo_key="p3-demo-$suffix"
package_name="package-$suffix"
digest="$(printf 'phase3-demo-blob-%s' "$suffix" | shasum -a 256 | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
length_bytes=512
storage_key="phase3-demo/$repo_key/$digest"

echo "Step 1: issue admin PAT and create repository"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p3-admin-$suffix\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
admin_token="$(json_string_field "$response_body" "token")"

if [ -z "$admin_token" ]; then
  echo "Failed to parse admin token."
  exit 1
fi

curl_call "POST" "/v1/repos" \
  "{\"RepoKey\":\"$repo_key\",\"RepoType\":\"local\",\"UpstreamUrl\":\"\",\"MemberRepos\":[]}" \
  -H "Authorization: Bearer $admin_token"
assert_status "201"

echo "Step 2: issue write/promote tokens"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p3-writer-$suffix\",\"Scopes\":[\"repo:$repo_key:write\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
write_token="$(json_string_field "$response_body" "token")"

curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p3-promote-$suffix\",\"Scopes\":[\"repo:$repo_key:promote\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
promote_token="$(json_string_field "$response_body" "token")"

if [ -z "$write_token" ] || [ -z "$promote_token" ]; then
  echo "Failed to parse write/promote tokens."
  exit 1
fi

echo "Step 3: create draft version"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/drafts" \
  "{\"PackageType\":\"nuget\",\"PackageNamespace\":\"\",\"PackageName\":\"$package_name\",\"Version\":\"1.0.0\"}" \
  -H "Authorization: Bearer $write_token"
assert_status "201"
version_id="$(json_string_field "$response_body" "versionId")"

if [ -z "$version_id" ]; then
  echo "Failed to parse version id."
  exit 1
fi

echo "Step 4: seed committed blob metadata for artifact entry linkage"
sql_exec "insert into blobs (digest, length_bytes, storage_key) values ('$digest', $length_bytes, '$storage_key') on conflict (digest) do update set storage_key = excluded.storage_key;"
sql_exec "insert into upload_sessions (upload_id, tenant_id, repo_id, expected_digest, expected_length, state, committed_blob_digest, created_by_subject, expires_at, committed_at) values (gen_random_uuid(), (select tenant_id from tenants where slug = 'default' limit 1), (select repo_id from repos where repo_key = '$repo_key' and tenant_id = (select tenant_id from tenants where slug = 'default' limit 1) limit 1), '$digest', $length_bytes, 'committed', '$digest', 'phase3-demo', now() + interval '1 hour', now());"

echo "Step 5: upsert artifact entries"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/$version_id/entries" \
  "{\"Entries\":[{\"RelativePath\":\"lib/$package_name.1.0.0.nupkg\",\"BlobDigest\":\"$digest\",\"ChecksumSha1\":\"\",\"ChecksumSha256\":\"$digest\",\"SizeBytes\":$length_bytes}]}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"

if ! contains_text "$response_body" "\"entryCount\":1"; then
  echo "Expected one persisted artifact entry. Body: $response_body"
  exit 1
fi

echo "Step 6: upsert and query manifest"
curl_call "PUT" "/v1/repos/$repo_key/packages/versions/$version_id/manifest" \
  "{\"Manifest\":{\"id\":\"$package_name\",\"version\":\"1.0.0\",\"authors\":[\"phase3-demo\"]},\"ManifestBlobDigest\":\"\"}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"

curl_call "GET" "/v1/repos/$repo_key/packages/versions/$version_id/manifest" "" \
  -H "Authorization: Bearer $write_token"
assert_status "200"

if ! contains_text "$response_body" "\"packageType\":\"nuget\""; then
  echo "Expected nuget manifest payload. Body: $response_body"
  exit 1
fi

echo "Step 7: publish and verify outbox emission"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/$version_id/publish" "" \
  -H "Authorization: Bearer $promote_token"
assert_status "200"

if ! contains_text "$response_body" "\"state\":\"published\""; then
  echo "Expected published state. Body: $response_body"
  exit 1
fi

if ! contains_text "$response_body" "\"eventEmitted\":true"; then
  echo "Expected first publish to emit outbox event. Body: $response_body"
  exit 1
fi

echo "Step 8: idempotent publish retry"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/$version_id/publish" "" \
  -H "Authorization: Bearer $promote_token"
assert_status "200"

if ! contains_text "$response_body" "\"idempotent\":true"; then
  echo "Expected idempotent publish response. Body: $response_body"
  exit 1
fi

outbox_count="$(sql_scalar "select count(*) from outbox_events where aggregate_type = 'package_version' and aggregate_id = '$version_id' and event_type = 'version.published';")"
if [ "$outbox_count" != "1" ]; then
  echo "Expected exactly one version.published outbox event, got '$outbox_count'."
  exit 1
fi

echo "Step 9: audit verification"
curl_call "GET" "/v1/audit?limit=200" "" \
  -H "Authorization: Bearer $admin_token"
assert_status "200"

for expected in "package.version.entries.upserted" "package.version.manifest.upserted" "package.version.published"; do
  if ! contains_text "$response_body" "$expected"; then
    echo "Missing expected audit action '$expected'."
    exit 1
  fi
done

echo "Phase 3 demo completed successfully."
