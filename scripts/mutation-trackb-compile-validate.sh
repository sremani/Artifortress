#!/usr/bin/env bash
set -euo pipefail

run_spike="${RUN_SPIKE:-false}"
project_path="${PROJECT_PATH:-src/Artifortress.Domain/Artifortress.Domain.fsproj}"
source_file="${SOURCE_FILE:-src/Artifortress.Domain/Library.fs}"
max_mutants="${MAX_MUTANTS:-9}"
result_json="${RESULT_JSON:-artifacts/mutation/mut07c-compile-validation.json}"
report_path="${REPORT_PATH:-docs/reports/mutation-trackb-mut07c-compile-validation.md}"
summary_path="${SUMMARY_PATH:-artifacts/ci/mutation-trackb-compile-validate-summary.txt}"
metrics_json_path="${METRICS_JSON_PATH:-artifacts/ci/mutation-trackb-compile-validate-metrics.json}"

if [ "$run_spike" = "true" ]; then
  ./scripts/mutation-trackb-spike.sh
fi

set +e
dotnet run --project tools/Artifortress.MutationTrack/Artifortress.MutationTrack.fsproj -- validate-fsharp-mutants \
  --project "$project_path" \
  --source-file "$source_file" \
  --max-mutants "$max_mutants" \
  --report "$report_path" \
  --result-json "$result_json"
validation_exit=$?
set -e

if [ ! -f "$result_json" ]; then
  echo "[mutation-trackb-compile-validate] Missing result json: $result_json" >&2
  exit 1
fi

extract_json_number() {
  local key="$1"
  local file="$2"
  local value
  value="$(sed -n -E "s/.*\"${key}\"[[:space:]]*:[[:space:]]*([0-9]+).*/\\1/p" "$file" | head -n 1 || true)"
  printf "%s" "${value:-0}"
}

discovered_count="$(extract_json_number "totalCandidatesDiscovered" "$result_json")"
selected_count="$(extract_json_number "selectedMutantCount" "$result_json")"
success_count="$(extract_json_number "successfulCompileCount" "$result_json")"
failed_count="$(extract_json_number "failedCompileCount" "$result_json")"

mkdir -p "$(dirname "$summary_path")"
mkdir -p "$(dirname "$metrics_json_path")"

cat > "$summary_path" <<EOF_SUMMARY
mutation_trackb_compile_validation_exit=$validation_exit
discovered_count=$discovered_count
selected_count=$selected_count
success_count=$success_count
failed_count=$failed_count
result_json=$result_json
report_path=$report_path
EOF_SUMMARY

cat > "$metrics_json_path" <<EOF_METRICS
{
  "exitCode": $validation_exit,
  "totalCandidatesDiscovered": $discovered_count,
  "selectedMutantCount": $selected_count,
  "successfulCompileCount": $success_count,
  "failedCompileCount": $failed_count
}
EOF_METRICS

if [ "$selected_count" -le 0 ]; then
  echo "[mutation-trackb-compile-validate] No mutants were selected for compile validation." >&2
  exit 1
fi

if [ "$failed_count" -ne 0 ] || [ "$validation_exit" -ne 0 ]; then
  echo "[mutation-trackb-compile-validate] Compile validation failed: selected=$selected_count success=$success_count failed=$failed_count" >&2
  exit 1
fi

echo "[mutation-trackb-compile-validate] PASS: discovered=$discovered_count selected=$selected_count success=$success_count"
