#!/usr/bin/env bash
set -euo pipefail

report_path="${REPORT_PATH:-docs/reports/performance-workflow-baseline-latest.md}"
test_project="${TEST_PROJECT:-tests/Kublai.Domain.Tests/Kublai.Domain.Tests.fsproj}"
configuration="${CONFIGURATION:-Debug}"

workloads=(
  "ER-701 publish workflow baseline batch completes|24|publish completions"
  "ER-701 search query baseline batch completes|72|search queries"
  "ER-701 quarantine workflow baseline batch completes|30|quarantine workflow operations"
)

mkdir -p "$(dirname "$report_path")"

now_ms() {
  perl -MTime::HiRes=time -e 'printf "%.0f\n", time()*1000'
}

format_ops_per_second() {
  local count="$1"
  local ms="$2"
  awk -v c="$count" -v t="$ms" 'BEGIN { if (t <= 0) { printf "0.00"; } else { printf "%.2f", c / (t / 1000.0); } }'
}

results=()
overall_status="PASS"

for workload in "${workloads[@]}"; do
  drill_name="${workload%%|*}"
  remainder="${workload#*|}"
  operation_count="${remainder%%|*}"
  operation_label="${remainder#*|}"

  echo "[performance-workflow-baseline] Running: ${drill_name}"
  start_ms="$(now_ms)"

  if dotnet test "$test_project" --configuration "$configuration" --no-build -v minimal --filter "FullyQualifiedName~${drill_name}"; then
    end_ms="$(now_ms)"
    elapsed_ms="$((end_ms - start_ms))"
    ops_per_second="$(format_ops_per_second "$operation_count" "$elapsed_ms")"
    results+=("PASS|${drill_name}|${operation_count}|${operation_label}|${elapsed_ms}|${ops_per_second}")
  else
    overall_status="FAIL"
    end_ms="$(now_ms)"
    elapsed_ms="$((end_ms - start_ms))"
    results+=("FAIL|${drill_name}|${operation_count}|${operation_label}|${elapsed_ms}|0.00")
  fi
done

timestamp_utc="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

{
  echo "# Performance Workflow Baseline Report"
  echo
  echo "Generated at: ${timestamp_utc}"
  echo
  echo "## Summary"
  echo
  echo "- overall status: ${overall_status}"
  echo "- upload/download reference report: \`docs/reports/phase2-load-baseline-latest.md\`"
  echo
  echo "## Results"
  echo
  echo "| Workload | Status | Operation count | Operation label | Elapsed (ms) | Ops/sec |"
  echo "|---|---|---:|---|---:|---:|"

  for result in "${results[@]}"; do
    status="${result%%|*}"
    payload="${result#*|}"
    name="${payload%%|*}"
    payload="${payload#*|}"
    count="${payload%%|*}"
    payload="${payload#*|}"
    label="${payload%%|*}"
    payload="${payload#*|}"
    elapsed="${payload%%|*}"
    ops="${payload#*|}"
    echo "| ${name} | ${status} | ${count} | ${label} | ${elapsed} | ${ops} |"
  done

  echo
  echo "## Reproduce"
  echo
  echo '```bash'
  echo "make build"
  echo "make test-integration"
  echo "make performance-workflow-baseline"
  echo '```'
} > "$report_path"

echo "[performance-workflow-baseline] Report written to $report_path"

if [ "$overall_status" != "PASS" ]; then
  echo "[performance-workflow-baseline] Baseline failed."
  exit 1
fi

echo "[performance-workflow-baseline] Baseline passed."
