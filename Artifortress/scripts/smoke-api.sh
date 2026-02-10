#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8085}"
log_file="/tmp/artifortress-api-smoke.log"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"

if [ ! -f "$api_dll" ]; then
  echo "Missing API binary at $api_dll. Run 'make build' first."
  exit 1
fi

dotnet "$api_dll" --urls "$api_url" >"$log_file" 2>&1 &
api_pid=$!

cleanup() {
  kill "$api_pid" >/dev/null 2>&1 || true
}
trap cleanup EXIT

for _ in {1..30}; do
  if curl -sf "$api_url/health/live" >/dev/null; then
    curl -sf "$api_url/health/ready" >/dev/null
    echo "API smoke passed."
    exit 0
  fi
  sleep 1
done

echo "API smoke failed. Logs:"
cat "$log_file"
exit 1
