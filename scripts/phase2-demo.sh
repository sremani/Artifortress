#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8086}"
connection_string="${CONNECTION_STRING:-Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress}"
bootstrap_token="${BOOTSTRAP_TOKEN:-phase2-demo-bootstrap}"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"
log_file="/tmp/artifortress-phase2-demo.log"

resolve_dotnet_bin() {
  local candidate=""

  if [ -n "${DOTNET_BIN:-}" ] && [ -x "${DOTNET_BIN}" ]; then
    if "${DOTNET_BIN}" --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App 10\\."; then
      printf '%s' "${DOTNET_BIN}"
      return 0
    fi
  fi

  for candidate in "$HOME/.dotnet/dotnet" "$(command -v dotnet 2>/dev/null)" "/usr/local/share/dotnet/dotnet"; do
    if [ -n "$candidate" ] && [ -x "$candidate" ]; then
      if "$candidate" --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App 10\\."; then
        printf '%s' "$candidate"
        return 0
      fi
    fi
  done

  echo "Could not locate a dotnet binary with Microsoft.NETCore.App 10.x runtime." >&2
  return 1
}

dotnet_bin="$(resolve_dotnet_bin)"

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

curl_download_to_file() {
  local method="$1"
  local path="$2"
  local output_file="$3"
  local headers_file="$4"
  shift 4

  response_status="$(curl -sS -D "$headers_file" -o "$output_file" -w "%{http_code}" -X "$method" "$api_url$path" "$@")"
  response_body=""
}

assert_status() {
  local expected="$1"
  if [ "$response_status" != "$expected" ]; then
    echo "Expected HTTP $expected but got $response_status."
    if [ -n "$response_body" ]; then
      echo "Response body: $response_body"
    fi
    exit 1
  fi
}

json_string_field() {
  local json="$1"
  local field="$2"
  printf '%s' "$json" | sed -n "s/.*\"$field\":\"\\([^\"]*\\)\".*/\\1/p"
}

json_number_field() {
  local json="$1"
  local field="$2"
  printf '%s' "$json" | sed -n "s/.*\"$field\":\\([0-9][0-9]*\\).*/\\1/p"
}

contains_text() {
  local haystack="$1"
  local needle="$2"
  printf '%s' "$haystack" | grep -Fq "$needle"
}

sha256_file() {
  local file_path="$1"
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$file_path" | awk '{print $1}' | tr '[:upper:]' '[:lower:]'
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$file_path" | awk '{print $1}' | tr '[:upper:]' '[:lower:]'
  else
    openssl dgst -sha256 -r "$file_path" | awk '{print $1}' | tr '[:upper:]' '[:lower:]'
  fi
}

file_size_bytes() {
  local file_path="$1"
  wc -c < "$file_path" | tr -d '[:space:]'
}

upload_part_and_get_etag() {
  local upload_url="$1"
  local file_path="$2"

  local headers_file
  headers_file="$(mktemp)"

  local status
  status="$(curl -sS -o /dev/null -D "$headers_file" -w "%{http_code}" -X PUT "$upload_url" --data-binary @"$file_path")"

  if [ "$status" != "200" ] && [ "$status" != "201" ] && [ "$status" != "204" ]; then
    echo "Unexpected status from pre-signed upload URL: $status"
    cat "$headers_file"
    rm -f "$headers_file"
    exit 1
  fi

  local etag
  etag="$(tr -d '\r' < "$headers_file" | awk 'tolower($1)=="etag:" {print $2}' | head -n1 | tr -d '"')"
  rm -f "$headers_file"

  if [ -z "$etag" ]; then
    echo "Missing ETag header from pre-signed upload response."
    exit 1
  fi

  printf '%s' "$etag"
}

echo "Starting API at $api_url"
ConnectionStrings__Postgres="$connection_string" Auth__BootstrapToken="$bootstrap_token" \
  "$dotnet_bin" "$api_dll" --urls "$api_url" >"$log_file" 2>&1 &
api_pid=$!

payload_file="$(mktemp)"
mismatch_file="$(mktemp)"
download_file="$(mktemp)"
range_file="$(mktemp)"
expected_range_file="$(mktemp)"
range_headers_file="$(mktemp)"
full_headers_file="$(mktemp)"

cleanup() {
  kill "$api_pid" >/dev/null 2>&1 || true
  rm -f "$payload_file" "$mismatch_file" "$download_file" "$range_file" "$expected_range_file" "$range_headers_file" "$full_headers_file"
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
repo_key="p2-demo-$suffix"

printf 'phase2-demo-primary-%s\n' "$suffix" > "$payload_file"
head -c 65536 /dev/urandom >> "$payload_file"
cp "$payload_file" "$mismatch_file"
printf 'X' | dd of="$mismatch_file" bs=1 seek=0 conv=notrunc status=none

expected_digest="$(sha256_file "$payload_file")"
expected_length="$(file_size_bytes "$payload_file")"
mismatch_digest="$(sha256_file "$mismatch_file")"

echo "Step 1: issue admin PAT and create repository"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p2-admin-$suffix\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}" \
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

echo "Step 2: issue write/read PATs"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p2-writer-$suffix\",\"Scopes\":[\"repo:$repo_key:write\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
write_token="$(json_string_field "$response_body" "token")"

curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p2-reader-$suffix\",\"Scopes\":[\"repo:$repo_key:read\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
read_token="$(json_string_field "$response_body" "token")"

if [ -z "$write_token" ] || [ -z "$read_token" ]; then
  echo "Failed to parse write/read PATs."
  exit 1
fi

echo "Step 3: upload create -> part -> complete -> commit"
curl_call "POST" "/v1/repos/$repo_key/uploads" \
  "{\"ExpectedDigest\":\"$expected_digest\",\"ExpectedLength\":$expected_length}" \
  -H "Authorization: Bearer $write_token"
assert_status "201"
upload_id="$(json_string_field "$response_body" "uploadId")"
create_state="$(json_string_field "$response_body" "state")"

if [ "$create_state" != "initiated" ]; then
  echo "Expected initiated state on upload create, got '$create_state'."
  exit 1
fi

if contains_text "$response_body" "\"deduped\":true"; then
  echo "Expected initial upload create to be non-deduped."
  exit 1
fi

curl_call "POST" "/v1/repos/$repo_key/uploads/$upload_id/parts" \
  "{\"PartNumber\":1}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"
upload_url="$(json_string_field "$response_body" "uploadUrl")"
part_state="$(json_string_field "$response_body" "state")"

if [ "$part_state" != "parts_uploading" ] || [ -z "$upload_url" ]; then
  echo "Unexpected part response: $response_body"
  exit 1
fi

etag="$(upload_part_and_get_etag "$upload_url" "$payload_file")"

curl_call "POST" "/v1/repos/$repo_key/uploads/$upload_id/complete" \
  "{\"Parts\":[{\"PartNumber\":1,\"ETag\":\"$etag\"}]}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"
complete_state="$(json_string_field "$response_body" "state")"

if [ "$complete_state" != "pending_commit" ]; then
  echo "Expected pending_commit state on complete, got '$complete_state'."
  exit 1
fi

curl_call "POST" "/v1/repos/$repo_key/uploads/$upload_id/commit" "" \
  -H "Authorization: Bearer $write_token"
assert_status "200"
commit_state="$(json_string_field "$response_body" "state")"
commit_digest="$(json_string_field "$response_body" "digest")"
commit_length="$(json_number_field "$response_body" "length")"

if [ "$commit_state" != "committed" ] || [ "$commit_digest" != "$expected_digest" ] || [ "$commit_length" != "$expected_length" ]; then
  echo "Unexpected commit response: $response_body"
  exit 1
fi

echo "Step 4: full and ranged download validation"
curl_download_to_file "GET" "/v1/repos/$repo_key/blobs/$expected_digest" "$download_file" "$full_headers_file" \
  -H "Authorization: Bearer $read_token"
assert_status "200"
if ! cmp -s "$payload_file" "$download_file"; then
  echo "Full download content mismatch."
  exit 1
fi

curl_download_to_file "GET" "/v1/repos/$repo_key/blobs/$expected_digest" "$range_file" "$range_headers_file" \
  -H "Authorization: Bearer $read_token" \
  -H "Range: bytes=5-14"
assert_status "206"
dd if="$payload_file" of="$expected_range_file" bs=1 skip=5 count=10 status=none
if ! cmp -s "$expected_range_file" "$range_file"; then
  echo "Range download content mismatch."
  exit 1
fi

echo "Step 5: dedupe path returns committed session on second create"
curl_call "POST" "/v1/repos/$repo_key/uploads" \
  "{\"ExpectedDigest\":\"$expected_digest\",\"ExpectedLength\":$expected_length}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"

if ! contains_text "$response_body" "\"deduped\":true" || ! contains_text "$response_body" "\"state\":\"committed\""; then
  echo "Expected deduped committed response on second create. Body: $response_body"
  exit 1
fi

echo "Step 6: mismatch commit returns deterministic verification error"
curl_call "POST" "/v1/repos/$repo_key/uploads" \
  "{\"ExpectedDigest\":\"$mismatch_digest\",\"ExpectedLength\":$expected_length}" \
  -H "Authorization: Bearer $write_token"
assert_status "201"
mismatch_upload_id="$(json_string_field "$response_body" "uploadId")"

curl_call "POST" "/v1/repos/$repo_key/uploads/$mismatch_upload_id/parts" \
  "{\"PartNumber\":1}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"
mismatch_upload_url="$(json_string_field "$response_body" "uploadUrl")"
mismatch_etag="$(upload_part_and_get_etag "$mismatch_upload_url" "$payload_file")"

curl_call "POST" "/v1/repos/$repo_key/uploads/$mismatch_upload_id/complete" \
  "{\"Parts\":[{\"PartNumber\":1,\"ETag\":\"$mismatch_etag\"}]}" \
  -H "Authorization: Bearer $write_token"
assert_status "200"

curl_call "POST" "/v1/repos/$repo_key/uploads/$mismatch_upload_id/commit" "" \
  -H "Authorization: Bearer $write_token"
assert_status "409"

if ! contains_text "$response_body" "\"error\":\"upload_verification_failed\""; then
  echo "Expected upload_verification_failed response. Body: $response_body"
  exit 1
fi

echo "Step 7: upload lifecycle actions are present in audit log"
curl_call "GET" "/v1/audit?limit=300" "" -H "Authorization: Bearer $admin_token"
assert_status "200"

for action in "upload.session.created" "upload.part.presigned" "upload.completed" "upload.committed" "upload.commit.verification_failed"; do
  if ! contains_text "$response_body" "$action"; then
    echo "Expected audit action '$action' in response."
    exit 1
  fi
done

echo "Phase 2 demo completed successfully."
