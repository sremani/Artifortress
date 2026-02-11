#!/usr/bin/env bash
set -euo pipefail

source_file="${SOURCE_FILE:-src/Artifortress.Domain/Library.fs}"
test_project="${TEST_PROJECT:-tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj}"
max_mutants="${MAX_MUTANTS:-12}"
scratch_root="${SCRATCH_ROOT:-artifacts/mutation/native-fsharp-runtime}"
report_path="${REPORT_PATH:-docs/reports/mutation-native-fsharp-latest.md}"
result_json="${RESULT_JSON:-artifacts/mutation/mutation-native-fsharp-latest.json}"
summary_path="${SUMMARY_PATH:-artifacts/ci/mutation-native-fsharp-summary.txt}"
metrics_json_path="${METRICS_JSON_PATH:-artifacts/ci/mutation-native-fsharp-metrics.json}"

set +e
dotnet run --project tools/Artifortress.MutationTrack/Artifortress.MutationTrack.fsproj -- run-fsharp-native \
  --source-file "$source_file" \
  --test-project "$test_project" \
  --max-mutants "$max_mutants" \
  --scratch-root "$scratch_root" \
  --report "$report_path" \
  --result-json "$result_json"
native_exit=$?
set -e

if [ ! -f "$result_json" ]; then
  echo "[mutation-fsharp-native-run] Missing result json: $result_json" >&2
  exit 1
fi

extract_json_number() {
  local key="$1"
  local file="$2"
  local value
  value="$(sed -n -E "s/.*\"${key}\"[[:space:]]*:[[:space:]]*([0-9]+).*/\\1/p" "$file" | head -n 1 || true)"
  printf "%s" "${value:-0}"
}

selected_count="$(extract_json_number "selectedMutantCount" "$result_json")"
killed_count="$(extract_json_number "killedCount" "$result_json")"
survived_count="$(extract_json_number "survivedCount" "$result_json")"
compile_error_count="$(extract_json_number "compileErrorCount" "$result_json")"
infra_error_count="$(extract_json_number "infrastructureErrorCount" "$result_json")"

mkdir -p "$(dirname "$summary_path")"
mkdir -p "$(dirname "$metrics_json_path")"

cat > "$summary_path" <<EOF_SUMMARY
native_mutation_exit=$native_exit
selected_count=$selected_count
killed_count=$killed_count
survived_count=$survived_count
compile_error_count=$compile_error_count
infrastructure_error_count=$infra_error_count
result_json=$result_json
report_path=$report_path
EOF_SUMMARY

cat > "$metrics_json_path" <<EOF_METRICS
{
  "exitCode": $native_exit,
  "selectedMutantCount": $selected_count,
  "killedCount": $killed_count,
  "survivedCount": $survived_count,
  "compileErrorCount": $compile_error_count,
  "infrastructureErrorCount": $infra_error_count
}
EOF_METRICS

if [ "$selected_count" -le 0 ]; then
  echo "[mutation-fsharp-native-run] No mutants selected." >&2
  exit 1
fi

if [ "$native_exit" -ne 0 ]; then
  echo "[mutation-fsharp-native-run] Native runtime failed: selected=$selected_count killed=$killed_count survived=$survived_count compile_errors=$compile_error_count infra_errors=$infra_error_count" >&2
  exit 1
fi

echo "[mutation-fsharp-native-run] PASS: selected=$selected_count killed=$killed_count survived=$survived_count"
