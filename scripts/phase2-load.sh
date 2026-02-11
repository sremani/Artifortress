#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8086}"
connection_string="${CONNECTION_STRING:-Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress}"
bootstrap_token="${BOOTSTRAP_TOKEN:-phase2-load-bootstrap}"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"
log_file="/tmp/artifortress-phase2-load.log"

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

upload_iterations="${UPLOAD_ITERATIONS:-12}"
download_iterations="${DOWNLOAD_ITERATIONS:-36}"
payload_bytes="${PAYLOAD_BYTES:-262144}"
upload_target_mbps="${UPLOAD_TARGET_MBPS:-4.00}"
download_target_mbps="${DOWNLOAD_TARGET_MBPS:-6.00}"
enforce_targets="${ENFORCE_TARGETS:-false}"
report_path="${REPORT_PATH:-docs/reports/phase2-load-baseline-latest.md}"

if [ ! -f "$api_dll" ]; then
  echo "Missing API binary at $api_dll. Run 'make build' first."
  exit 1
fi

is_positive_int() {
  case "$1" in
    ''|*[!0-9]*)
      return 1
      ;;
    *)
      [ "$1" -gt 0 ]
      ;;
  esac
}

if ! is_positive_int "$upload_iterations"; then
  echo "UPLOAD_ITERATIONS must be a positive integer (current: $upload_iterations)."
  exit 1
fi

if ! is_positive_int "$download_iterations"; then
  echo "DOWNLOAD_ITERATIONS must be a positive integer (current: $download_iterations)."
  exit 1
fi

if ! is_positive_int "$payload_bytes"; then
  echo "PAYLOAD_BYTES must be a positive integer (current: $payload_bytes)."
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

now_ms() {
  perl -MTime::HiRes=time -e 'printf "%.0f\n", time()*1000'
}

float_lt() {
  local left="$1"
  local right="$2"
  awk -v a="$left" -v b="$right" 'BEGIN { if ((a + 0.0) < (b + 0.0)) exit 0; exit 1 }'
}

format_mbps() {
  local bytes="$1"
  local ms="$2"
  awk -v b="$bytes" -v t="$ms" 'BEGIN { if (t <= 0) { printf "0.00"; } else { printf "%.2f", (b / 1048576.0) / (t / 1000.0); } }'
}

format_rps() {
  local count="$1"
  local ms="$2"
  awk -v c="$count" -v t="$ms" 'BEGIN { if (t <= 0) { printf "0.00"; } else { printf "%.2f", c / (t / 1000.0); } }'
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

download_blob_metrics() {
  local repo_key="$1"
  local digest="$2"
  local token="$3"
  curl -sS -o /dev/null -w "%{http_code}:%{size_download}" "$api_url/v1/repos/$repo_key/blobs/$digest" -H "Authorization: Bearer $token"
}

cpu_model="unknown"
if cpu_model_raw="$(sysctl -n machdep.cpu.brand_string 2>/dev/null)"; then
  if [ -n "$cpu_model_raw" ]; then
    cpu_model="$cpu_model_raw"
  fi
elif command -v lscpu >/dev/null 2>&1; then
  cpu_model_raw="$(lscpu 2>/dev/null | awk -F: '/Model name/ {gsub(/^[ \t]+/, "", $2); print $2; exit}')"
  if [ -n "$cpu_model_raw" ]; then
    cpu_model="$cpu_model_raw"
  fi
fi

memory_gib="unknown"
if memory_bytes_raw="$(sysctl -n hw.memsize 2>/dev/null)"; then
  if [ -n "$memory_bytes_raw" ]; then
    memory_gib="$(awk -v b="$memory_bytes_raw" 'BEGIN { printf "%.2f", b / 1073741824.0 }')"
  fi
elif command -v free >/dev/null 2>&1; then
  memory_bytes_raw="$(free -b 2>/dev/null | awk '/Mem:/ {print $2; exit}')"
  if [ -n "$memory_bytes_raw" ]; then
    memory_gib="$(awk -v b="$memory_bytes_raw" 'BEGIN { printf "%.2f", b / 1073741824.0 }')"
  fi
fi

echo "Starting API at $api_url"
ConnectionStrings__Postgres="$connection_string" Auth__BootstrapToken="$bootstrap_token" \
  "$dotnet_bin" "$api_dll" --urls "$api_url" >"$log_file" 2>&1 &
api_pid=$!

temp_dir="$(mktemp -d)"
declare -a payload_files
declare -a payload_digests
declare -a payload_sizes

cleanup() {
  kill "$api_pid" >/dev/null 2>&1 || true
  rm -rf "$temp_dir"
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
repo_key="p2-load-$suffix"

echo "Issuing PATs and creating throughput test repository"
curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p2-load-admin-$suffix\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
admin_token="$(json_string_field "$response_body" "token")"

curl_call "POST" "/v1/repos" \
  "{\"RepoKey\":\"$repo_key\",\"RepoType\":\"local\",\"UpstreamUrl\":\"\",\"MemberRepos\":[]}" \
  -H "Authorization: Bearer $admin_token"
assert_status "201"

curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p2-load-writer-$suffix\",\"Scopes\":[\"repo:$repo_key:write\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
write_token="$(json_string_field "$response_body" "token")"

curl_call "POST" "/v1/auth/pats" \
  "{\"Subject\":\"p2-load-reader-$suffix\",\"Scopes\":[\"repo:$repo_key:read\"],\"TtlMinutes\":60}" \
  -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
read_token="$(json_string_field "$response_body" "token")"

if [ -z "$admin_token" ] || [ -z "$write_token" ] || [ -z "$read_token" ]; then
  echo "Failed to parse PATs."
  exit 1
fi

echo "Preparing payload set (count=$upload_iterations, bytes=$payload_bytes)"
upload_total_bytes=0
for i in $(seq 1 "$upload_iterations"); do
  file_path="$temp_dir/payload-$i.bin"
  head -c "$payload_bytes" /dev/urandom > "$file_path"
  length_bytes="$(wc -c < "$file_path" | tr -d '[:space:]')"
  payload_files+=("$file_path")
  payload_digests+=("$(sha256_file "$file_path")")
  payload_sizes+=("$length_bytes")
  upload_total_bytes=$((upload_total_bytes + length_bytes))
done

echo "Running upload throughput loop"
upload_total_ms=0
for i in $(seq 1 "$upload_iterations"); do
  file_path="${payload_files[$((i - 1))]}"
  digest="${payload_digests[$((i - 1))]}"
  length_bytes="${payload_sizes[$((i - 1))]}"

  start_ms="$(now_ms)"

  curl_call "POST" "/v1/repos/$repo_key/uploads" \
    "{\"ExpectedDigest\":\"$digest\",\"ExpectedLength\":$length_bytes}" \
    -H "Authorization: Bearer $write_token"
  assert_status "201"
  upload_id="$(json_string_field "$response_body" "uploadId")"

  curl_call "POST" "/v1/repos/$repo_key/uploads/$upload_id/parts" \
    "{\"PartNumber\":1}" \
    -H "Authorization: Bearer $write_token"
  assert_status "200"
  upload_url="$(json_string_field "$response_body" "uploadUrl")"
  etag="$(upload_part_and_get_etag "$upload_url" "$file_path")"

  curl_call "POST" "/v1/repos/$repo_key/uploads/$upload_id/complete" \
    "{\"Parts\":[{\"PartNumber\":1,\"ETag\":\"$etag\"}]}" \
    -H "Authorization: Bearer $write_token"
  assert_status "200"

  curl_call "POST" "/v1/repos/$repo_key/uploads/$upload_id/commit" "" \
    -H "Authorization: Bearer $write_token"
  assert_status "200"

  end_ms="$(now_ms)"
  upload_total_ms=$((upload_total_ms + end_ms - start_ms))
done

upload_mbps="$(format_mbps "$upload_total_bytes" "$upload_total_ms")"
upload_rps="$(format_rps "$upload_iterations" "$upload_total_ms")"

echo "Running download throughput loop"
download_total_ms=0
download_total_bytes=0
for i in $(seq 1 "$download_iterations"); do
  idx=$(( (i - 1) % upload_iterations ))
  digest="${payload_digests[$idx]}"
  expected_download_bytes="${payload_sizes[$idx]}"
  start_ms="$(now_ms)"
  metrics="$(download_blob_metrics "$repo_key" "$digest" "$read_token")"
  status="${metrics%%:*}"
  downloaded_bytes_raw="${metrics#*:}"
  downloaded_bytes="$(printf '%.0f' "$downloaded_bytes_raw")"
  if [ "$status" != "200" ]; then
    echo "Download request failed with status $status for digest $digest"
    exit 1
  fi
  if [ "$downloaded_bytes" -ne "$expected_download_bytes" ]; then
    echo "Download size mismatch for digest $digest: expected $expected_download_bytes bytes, got $downloaded_bytes bytes."
    exit 1
  fi
  download_total_bytes=$((download_total_bytes + downloaded_bytes))
  end_ms="$(now_ms)"
  download_total_ms=$((download_total_ms + end_ms - start_ms))
done

download_mbps="$(format_mbps "$download_total_bytes" "$download_total_ms")"
download_rps="$(format_rps "$download_iterations" "$download_total_ms")"

upload_target_result="PASS"
download_target_result="PASS"

if float_lt "$upload_mbps" "$upload_target_mbps"; then
  upload_target_result="WARN"
fi

if float_lt "$download_mbps" "$download_target_mbps"; then
  download_target_result="WARN"
fi

if [ "$enforce_targets" = "true" ]; then
  if [ "$upload_target_result" != "PASS" ] || [ "$download_target_result" != "PASS" ]; then
    echo "Throughput targets were not met and ENFORCE_TARGETS=true."
    echo "Upload:   ${upload_mbps} MiB/s (target ${upload_target_mbps})"
    echo "Download: ${download_mbps} MiB/s (target ${download_target_mbps})"
    exit 1
  fi
fi

mkdir -p "$(dirname "$report_path")"
timestamp_utc="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
dotnet_version="$("$dotnet_bin" --version)"

cat > "$report_path" <<EOF
# Phase 2 Throughput Baseline Report

Generated at: ${timestamp_utc}

## Workload

- Upload flow: create -> presign part -> upload part -> complete -> commit
- Upload iterations: ${upload_iterations}
- Download iterations: ${download_iterations}
- Payload size per object: ${payload_bytes} bytes
- Repository: ${repo_key}

## Environment

- OS: $(uname -srm)
- CPU: ${cpu_model}
- Memory (GiB): ${memory_gib}
- .NET SDK: ${dotnet_version}

## Targets

- Upload throughput target: >= ${upload_target_mbps} MiB/s
- Download throughput target: >= ${download_target_mbps} MiB/s

## Results

| Metric | Value |
|---|---|
| Upload total bytes | ${upload_total_bytes} |
| Upload elapsed (ms) | ${upload_total_ms} |
| Upload throughput (MiB/s) | ${upload_mbps} |
| Upload requests/sec | ${upload_rps} |
| Upload target result | ${upload_target_result} |
| Download total bytes | ${download_total_bytes} |
| Download elapsed (ms) | ${download_total_ms} |
| Download throughput (MiB/s) | ${download_mbps} |
| Download requests/sec | ${download_rps} |
| Download target result | ${download_target_result} |

## Reproduce

\`\`\`bash
make dev-up
make storage-bootstrap
make db-migrate
make build
make phase2-load
\`\`\`
EOF

echo "Phase 2 load baseline completed."
echo "Upload throughput:   ${upload_mbps} MiB/s (${upload_rps} req/s)"
echo "Download throughput: ${download_mbps} MiB/s (${download_rps} req/s)"
echo "Report: $report_path"
