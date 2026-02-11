#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8086}"
connection_string="${CONNECTION_STRING:-Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress}"
bootstrap_token="${BOOTSTRAP_TOKEN:-phase5-demo-bootstrap}"
db_user="${POSTGRES_USER:-artifortress}"
db_name="${POSTGRES_DB:-artifortress}"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"
api_log_file="/tmp/artifortress-phase5-demo-api.log"

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
repo_key="p5-demo-$suffix"
package_name="package-$suffix"
digest="$(printf 'phase5-demo-blob-%s' "$suffix" | shasum -a 256 | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
orphan_digest="$(printf 'phase5-demo-orphan-%s' "$suffix" | shasum -a 256 | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
length_bytes=512
storage_key="phase5-demo/$repo_key/$digest"

echo "Step 1: issue admin PAT and create repository"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p5-admin-$suffix\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}" \
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
  "{\"Subject\":\"p5-writer-$suffix\",\"Scopes\":[\"repo:$repo_key:write\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
write_token="$(json_string_field "$response_body" "token")"

curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p5-promote-$suffix\",\"Scopes\":[\"repo:$repo_key:promote\"],\"TtlMinutes\":60}" \
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

echo "Step 4: seed committed blob metadata"
sql_exec "insert into blobs (digest, length_bytes, storage_key) values ('$digest', $length_bytes, '$storage_key') on conflict (digest) do update set storage_key = excluded.storage_key;"
sql_exec "insert into upload_sessions (upload_id, tenant_id, repo_id, expected_digest, expected_length, state, committed_blob_digest, created_by_subject, expires_at, committed_at) values (gen_random_uuid(), (select tenant_id from tenants where slug = 'default' limit 1), (select repo_id from repos where repo_key = '$repo_key' and tenant_id = (select tenant_id from tenants where slug = 'default' limit 1) limit 1), '$digest', $length_bytes, 'committed', '$digest', 'phase5-demo', now() + interval '1 hour', now());"

echo "Step 5: upsert artifact entry + manifest and publish"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/$version_id/entries" \
  "{\"Entries\":[{\"RelativePath\":\"lib/$package_name.1.0.0.nupkg\",\"BlobDigest\":\"$digest\",\"ChecksumSha1\":\"\",\"ChecksumSha256\":\"$digest\",\"SizeBytes\":$length_bytes}]}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"

curl_call "PUT" "/v1/repos/$repo_key/packages/versions/$version_id/manifest" \
  "{\"Manifest\":{\"id\":\"$package_name\",\"version\":\"1.0.0\"},\"ManifestBlobDigest\":\"\"}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"

curl_call "POST" "/v1/repos/$repo_key/packages/versions/$version_id/publish" "" \
  -H "Authorization: Bearer $promote_token"
assert_status "200"

if ! contains_text "$response_body" "\"state\":\"published\""; then
  echo "Expected published version state. Body: $response_body"
  exit 1
fi

echo "Step 6: tombstone published version"
curl_call "POST" "/v1/repos/$repo_key/packages/versions/$version_id/tombstone" \
  "{\"Reason\":\"phase5 demo retention\",\"RetentionDays\":1}" \
  -H "Authorization: Bearer $promote_token"
assert_status "200"

if ! contains_text "$response_body" "\"state\":\"tombstoned\""; then
  echo "Expected tombstoned state. Body: $response_body"
  exit 1
fi

echo "Step 7: expire retention and seed orphan blob"
sql_exec "update tombstones set retention_until = now() - interval '1 hour' where version_id = '$version_id';"
sql_exec "insert into blobs (digest, length_bytes, storage_key) values ('$orphan_digest', 256, 'phase5-demo/orphan/$orphan_digest') on conflict (digest) do update set storage_key = excluded.storage_key;"

echo "Step 8: run GC dry-run"
curl_call "POST" "/v1/admin/gc/runs" \
  "{\"DryRun\":true,\"RetentionGraceHours\":0,\"BatchSize\":300}" \
  -H "Authorization: Bearer $admin_token"
assert_status "200"

if ! contains_text "$response_body" "\"mode\":\"dry_run\""; then
  echo "Expected dry_run mode. Body: $response_body"
  exit 1
fi

if ! contains_text "$response_body" "\"deletedBlobCount\":0"; then
  echo "Expected dry-run deletedBlobCount=0. Body: $response_body"
  exit 1
fi

echo "Step 9: run GC execute"
curl_call "POST" "/v1/admin/gc/runs" \
  "{\"DryRun\":false,\"RetentionGraceHours\":0,\"BatchSize\":300}" \
  -H "Authorization: Bearer $admin_token"
assert_status "200"

if ! contains_text "$response_body" "\"mode\":\"execute\""; then
  echo "Expected execute mode. Body: $response_body"
  exit 1
fi

version_state="$(sql_scalar "select coalesce((select state from package_versions where version_id = '$version_id'), '');")"
if [ -n "$version_state" ]; then
  echo "Expected version row hard-deleted after execute GC, found state '$version_state'."
  exit 1
fi

blob_exists="$(sql_scalar "select exists(select 1 from blobs where digest = '$digest');")"
orphan_exists="$(sql_scalar "select exists(select 1 from blobs where digest = '$orphan_digest');")"
upload_ref_count="$(sql_scalar "select count(*) from upload_sessions where committed_blob_digest = '$digest';")"

if [ "$blob_exists" != "f" ] || [ "$orphan_exists" != "f" ]; then
  echo "Expected both version blob and orphan blob to be deleted."
  echo "blob_exists=$blob_exists orphan_exists=$orphan_exists"
  exit 1
fi

if [ "$upload_ref_count" != "0" ]; then
  echo "Expected upload_sessions committed_blob_digest references to be cleared; count=$upload_ref_count"
  exit 1
fi

echo "Step 10: run reconcile summary"
curl_call "GET" "/v1/admin/reconcile/blobs?limit=20" "" \
  -H "Authorization: Bearer $admin_token"
assert_status "200"

if ! contains_text "$response_body" "\"orphanBlobCount\":"; then
  echo "Expected reconcile summary payload. Body: $response_body"
  exit 1
fi

echo "Step 11: verify audit actions"
curl_call "GET" "/v1/audit?limit=200" "" \
  -H "Authorization: Bearer $admin_token"
assert_status "200"

for expected in "package.version.tombstoned" "gc.run.dry_run" "gc.run.execute" "reconcile.blobs.checked"; do
  if ! contains_text "$response_body" "$expected"; then
    echo "Missing expected audit action '$expected'."
    exit 1
  fi
done

echo "Phase 5 demo completed successfully."
