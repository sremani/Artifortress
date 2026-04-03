#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8086}"
admin_token="${ADMIN_TOKEN:-}"
report_path="${REPORT_PATH:-docs/reports/enterprise-ops-drill-latest.md}"

if [ -z "$admin_token" ]; then
  echo "ADMIN_TOKEN is required"
  exit 1
fi

mkdir -p "$(dirname "$report_path")"

live_payload="$(curl -fsS "${api_url}/health/live")"
ready_payload="$(curl -fsS "${api_url}/health/ready")"
ops_payload="$(curl -fsS -H "Authorization: Bearer ${admin_token}" "${api_url}/v1/admin/ops/summary")"

./scripts/reliability-drill.sh

{
  echo "# Enterprise Operations Drill Report"
  echo
  echo "Generated at: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "## Health"
  echo
  echo '```json'
  echo "$live_payload"
  echo "$ready_payload"
  echo '```'
  echo
  echo "## Ops Summary"
  echo
  echo '```json'
  echo "$ops_payload"
  echo '```'
  echo
  echo "## Reliability Drill"
  echo
  echo "- see \`docs/reports/reliability-drill-latest.md\`"
} > "$report_path"

echo "[enterprise-ops-drill] Report written to $report_path"
