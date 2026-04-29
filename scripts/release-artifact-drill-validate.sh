#!/usr/bin/env bash
set -euo pipefail

phase6_report="${PHASE6_DRILL_REPORT:-docs/reports/phase6-rto-rpo-drill-latest.md}"
upgrade_report="${UPGRADE_DRILL_REPORT:-docs/reports/upgrade-compatibility-drill-latest.md}"

required_fields=(
  "release tag"
  "API image digest"
  "worker image digest"
  "Helm chart digest"
  "SBOM path"
  "release provenance report"
)

validate_report() {
  local report_path="$1"
  local label="$2"

  if [ ! -f "$report_path" ]; then
    echo "[release-artifact-drill-validate] Missing ${label} report: ${report_path}" >&2
    return 1
  fi

  for field in "${required_fields[@]}"; do
    if ! grep -q "^- ${field}: \`" "$report_path"; then
      echo "[release-artifact-drill-validate] Missing ${field} in ${label} report." >&2
      return 1
    fi

    if grep -q "^- ${field}: \`<unset>\`" "$report_path"; then
      echo "[release-artifact-drill-validate] ${field} is unset in ${label} report." >&2
      return 1
    fi
  done
}

validate_report "$phase6_report" "backup/restore"
validate_report "$upgrade_report" "upgrade"

echo "[release-artifact-drill-validate] PASS"
