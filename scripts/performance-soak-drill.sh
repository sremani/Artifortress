#!/usr/bin/env bash
set -euo pipefail

report_path="${REPORT_PATH:-docs/reports/performance-soak-latest.md}"
test_project="${TEST_PROJECT:-tests/Kublai.Domain.Tests/Kublai.Domain.Tests.fsproj}"
configuration="${CONFIGURATION:-Debug}"

drill_names=(
  "ER-404 large rebuild backfill completes without drift or starvation"
  "ER-702 mixed tenant sustained workload remains stable under load"
)

mkdir -p "$(dirname "$report_path")"

start_epoch="$(date +%s)"
results=()
overall_status="PASS"

for drill_name in "${drill_names[@]}"; do
  echo "[performance-soak-drill] Running: ${drill_name}"

  if dotnet test "$test_project" --configuration "$configuration" --no-build -v minimal --filter "FullyQualifiedName~${drill_name}"; then
    results+=("PASS|${drill_name}")
  else
    overall_status="FAIL"
    results+=("FAIL|${drill_name}")
  fi
done

total_duration="$(( $(date +%s) - start_epoch ))"

{
  echo "# Performance Soak Report"
  echo
  echo "Generated at: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "## Summary"
  echo
  echo "- overall status: ${overall_status}"
  echo "- total duration (seconds): ${total_duration}"
  echo
  echo "## Drill Results"
  echo

  for result in "${results[@]}"; do
    status="${result%%|*}"
    name="${result#*|}"
    echo "- ${status}: ${name}"
  done
} > "$report_path"

echo "[performance-soak-drill] Report written to $report_path"

if [ "$overall_status" != "PASS" ]; then
  echo "[performance-soak-drill] Soak drill failed."
  exit 1
fi

echo "[performance-soak-drill] Soak drill passed."
