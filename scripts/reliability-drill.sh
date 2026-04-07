#!/usr/bin/env bash
set -euo pipefail

report_path="${REPORT_PATH:-docs/reports/reliability-drill-latest.md}"
test_project="${TEST_PROJECT:-tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj}"
configuration="${CONFIGURATION:-Debug}"

drill_names=(
  "P4-04 outbox sweep enqueues search index job and marks event delivered"
  "ER-3-01 duplicate publish replay preserves single search document and job record"
  "ER-3-01 repeated malformed publish replay does not create search jobs or documents"
  "ER-3-03 stale processing search job is reclaimed after lease expiry"
  "ER-3-03 fresh processing search job is not reclaimed before lease expiry"
)

mkdir -p "$(dirname "$report_path")"

start_epoch="$(date +%s)"
results=()
overall_status="PASS"

for drill_name in "${drill_names[@]}"; do
  echo "[reliability-drill] Running: ${drill_name}"

  if dotnet test "$test_project" --configuration "$configuration" --no-build -v minimal --filter "FullyQualifiedName~${drill_name}"; then
    results+=("PASS|${drill_name}")
  else
    overall_status="FAIL"
    results+=("FAIL|${drill_name}")
  fi
done

total_duration="$(( $(date +%s) - start_epoch ))"

{
  echo "# Reliability Drill Report"
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

echo "[reliability-drill] Report written to $report_path"

if [ "$overall_status" != "PASS" ]; then
  echo "[reliability-drill] Drill failed."
  exit 1
fi

echo "[reliability-drill] Drill passed."
