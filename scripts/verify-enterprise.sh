#!/usr/bin/env bash
set -euo pipefail

run_step() {
  local label="$1"
  shift

  echo "[verify-enterprise] Running: ${label}"
  "$@"
  echo "[verify-enterprise] Passed: ${label}"
}

run_parallel_group() {
  local label="$1"
  shift

  echo "[verify-enterprise] Running parallel group: ${label}"

  local pids=()
  local names=()
  local index=0

  while [ "$#" -gt 0 ]; do
    local name="$1"
    local cmd="$2"
    shift 2

    /bin/bash -lc "$cmd" &
    pids+=("$!")
    names[index]="$name"
    index=$((index + 1))
  done

  local failed=0

  for idx in "${!pids[@]}"; do
    if wait "${pids[$idx]}"; then
      echo "[verify-enterprise] Passed: ${names[$idx]}"
    else
      echo "[verify-enterprise] Failed: ${names[$idx]}"
      failed=1
    fi
  done

  if [ "$failed" -ne 0 ]; then
    echo "[verify-enterprise] Parallel group failed: ${label}"
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

echo "[verify-enterprise] Enterprise verification battery passed."
