#!/usr/bin/env bash
set -euo pipefail

REPORT_PATH="${ENTERPRISE_VERIFICATION_REPORT:-docs/reports/enterprise-verification-latest.md}"
REPORT_DIR="$(dirname "$REPORT_PATH")"
STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
START_SECONDS="$(date -u +%s)"

mkdir -p "$REPORT_DIR"

GIT_COMMIT="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
GIT_BRANCH="$(git branch --show-current 2>/dev/null || echo unknown)"
SDK_VERSION="$(dotnet --version 2>/dev/null || echo unknown)"
RUNTIME_VERSION="$(dotnet --list-runtimes 2>/dev/null | awk '/Microsoft.NETCore.App/ { print $2; exit }')"
RUNTIME_VERSION="${RUNTIME_VERSION:-unknown}"

cat > "$REPORT_PATH" <<EOF
# Enterprise Verification Report

Generated at: ${STARTED_AT}

## Environment

- git commit: ${GIT_COMMIT}
- git branch: ${GIT_BRANCH}
- .NET SDK: ${SDK_VERSION}
- .NET runtime: ${RUNTIME_VERSION}

## Verification Steps

| Step | Command | Status | Duration Seconds |
|---|---|---:|---:|
EOF

append_step() {
  local label="$1"
  local command="$2"
  local status="$3"
  local duration="$4"

  printf '| %s | `%s` | %s | %s |\n' "$label" "$command" "$status" "$duration" >> "$REPORT_PATH"
}

finish_report() {
  local status="$1"
  local ended_at
  local ended_seconds
  local duration_seconds

  ended_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  ended_seconds="$(date -u +%s)"
  duration_seconds=$((ended_seconds - START_SECONDS))

  cat >> "$REPORT_PATH" <<EOF

## Summary

- overall status: ${status}
- started at: ${STARTED_AT}
- ended at: ${ended_at}
- total duration seconds: ${duration_seconds}

## Generated Evidence

- \`docs/reports/phase2-load-baseline-latest.md\`
- \`docs/reports/phase6-rto-rpo-drill-latest.md\`
- \`docs/reports/upgrade-compatibility-drill-latest.md\`
- \`docs/reports/reliability-drill-latest.md\`
- \`docs/reports/search-soak-drill-latest.md\`
- \`docs/reports/performance-workflow-baseline-latest.md\`
- \`docs/reports/performance-soak-latest.md\`
EOF
}

run_step() {
  local label="$1"
  shift
  local command="$*"
  local started
  local ended
  local duration

  echo "[verify-enterprise] Running: ${label}"
  started="$(date -u +%s)"
  if "$@"; then
    ended="$(date -u +%s)"
    duration=$((ended - started))
    append_step "$label" "$command" "PASS" "$duration"
  else
    ended="$(date -u +%s)"
    duration=$((ended - started))
    append_step "$label" "$command" "FAIL" "$duration"
    finish_report "FAIL"
    echo "[verify-enterprise] Failed: ${label}"
    echo "[verify-enterprise] Report written to ${REPORT_PATH}"
    exit 1
  fi
  echo "[verify-enterprise] Passed: ${label}"
}

run_parallel_group() {
  local label="$1"
  shift

  echo "[verify-enterprise] Running parallel group: ${label}"

  local pids=()
  local names=()
  local commands=()
  local starts=()
  local index=0

  while [ "$#" -gt 0 ]; do
    local name="$1"
    local cmd="$2"
    shift 2

    /bin/bash -lc "$cmd" &
    pids+=("$!")
    names[index]="$name"
    commands[index]="$cmd"
    starts[index]="$(date -u +%s)"
    index=$((index + 1))
  done

  local failed=0
  local ended
  local duration

  for idx in "${!pids[@]}"; do
    if wait "${pids[$idx]}"; then
      ended="$(date -u +%s)"
      duration=$((ended - starts[idx]))
      append_step "${names[$idx]}" "${commands[$idx]}" "PASS" "$duration"
      echo "[verify-enterprise] Passed: ${names[$idx]}"
    else
      ended="$(date -u +%s)"
      duration=$((ended - starts[idx]))
      append_step "${names[$idx]}" "${commands[$idx]}" "FAIL" "$duration"
      echo "[verify-enterprise] Failed: ${names[$idx]}"
      failed=1
    fi
  done

  if [ "$failed" -ne 0 ]; then
    echo "[verify-enterprise] Parallel group failed: ${label}"
    finish_report "FAIL"
    echo "[verify-enterprise] Report written to ${REPORT_PATH}"
    exit 1
  fi

  echo "[verify-enterprise] Parallel group passed: ${label}"
}

run_step "unit test suite" make test
run_step "integration test suite" make test-integration
run_step "phase2 throughput baseline" make phase2-load

run_parallel_group \
  "isolated database drills" \
  "upgrade compatibility drill" "bash scripts/upgrade-compatibility-drill.sh" \
  "phase6 backup and restore drill" "bash scripts/phase6-drill.sh"

run_step "reliability drill" bash scripts/reliability-drill.sh
run_step "search soak drill" bash scripts/search-soak-drill.sh
run_step "performance workflow baseline" make performance-workflow-baseline
run_step "performance soak drill" make performance-soak-drill

finish_report "PASS"
echo "[verify-enterprise] Report written to ${REPORT_PATH}"
echo "[verify-enterprise] Enterprise verification battery passed."
