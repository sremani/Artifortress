#!/usr/bin/env bash
set -euo pipefail

run_spike="${RUN_SPIKE:-true}"
result_json="${RESULT_JSON:-artifacts/mutation/mut06-trackb-latest.json}"
report_path="${REPORT_PATH:-docs/reports/mutation-trackb-mut06-latest.md}"
log_path="${LOG_PATH:-/tmp/artifortress-mutation-trackb-mut06.log}"
summary_path="${SUMMARY_PATH:-artifacts/ci/mutation-trackb-assert-summary.txt}"
metrics_json_path="${METRICS_JSON_PATH:-artifacts/ci/mutation-trackb-assert-metrics.json}"

if [ "$run_spike" = "true" ]; then
  ./scripts/mutation-trackb-spike.sh
fi

for required in "$result_json" "$report_path" "$log_path"; do
  if [ ! -f "$required" ]; then
    echo "[mutation-trackb-assert] Missing required artifact: $required" >&2
    exit 1
  fi
done

extract_json_string() {
  local key="$1"
  local file="$2"
  local value
  value="$(sed -n -E "s/.*\"${key}\"[[:space:]]*:[[:space:]]*\"([^\"]+)\".*/\\1/p" "$file" | head -n 1 || true)"
  printf "%s" "$value"
}

extract_json_number() {
  local key="$1"
  local file="$2"
  local value
  value="$(sed -n -E "s/.*\"${key}\"[[:space:]]*:[[:space:]]*([0-9]+).*/\\1/p" "$file" | head -n 1 || true)"
  printf "%s" "${value:-0}"
}

classification="$(extract_json_string "classification" "$result_json")"
candidate_count="$(extract_json_number "candidateCount" "$result_json")"
mapped_span_count="$(extract_json_number "mappedSpanCount" "$result_json")"
planned_mutant_count="$(extract_json_number "plannedMutantCount" "$result_json")"
created_mutant_count="$(extract_json_number "createdMutantCount" "$result_json")"
quarantined_compile_error_count="$(extract_json_number "quarantinedCompileErrorCount" "$result_json")"

if [ -z "$classification" ]; then
  echo "[mutation-trackb-assert] Could not parse classification from $result_json" >&2
  exit 1
fi

if [ "$classification" != "quarantined_compile_errors" ] && [ "$classification" != "success" ]; then
  echo "[mutation-trackb-assert] Unexpected classification: $classification" >&2
  exit 1
fi

if [ "$candidate_count" -le 0 ] || [ "$mapped_span_count" -le 0 ] || [ "$planned_mutant_count" -le 0 ] || [ "$created_mutant_count" -le 0 ]; then
  echo "[mutation-trackb-assert] Expected positive candidate/mapped/planned/created counts but got: candidates=$candidate_count mapped=$mapped_span_count planned=$planned_mutant_count created=$created_mutant_count" >&2
  exit 1
fi

if [ "$classification" = "quarantined_compile_errors" ] && [ "$quarantined_compile_error_count" -le 0 ]; then
  echo "[mutation-trackb-assert] Classification is quarantined_compile_errors but quarantinedCompileErrorCount=$quarantined_compile_error_count" >&2
  exit 1
fi

mkdir -p "$(dirname "$summary_path")"
mkdir -p "$(dirname "$metrics_json_path")"

cat > "$summary_path" <<EOF
mutation_trackb_assertion=pass
classification=$classification
candidate_count=$candidate_count
mapped_span_count=$mapped_span_count
planned_mutant_count=$planned_mutant_count
created_mutant_count=$created_mutant_count
quarantined_compile_error_count=$quarantined_compile_error_count
result_json=$result_json
report_path=$report_path
log_path=$log_path
EOF

cat > "$metrics_json_path" <<EOF
{
  "classification": "$classification",
  "candidateCount": $candidate_count,
  "mappedSpanCount": $mapped_span_count,
  "plannedMutantCount": $planned_mutant_count,
  "createdMutantCount": $created_mutant_count,
  "quarantinedCompileErrorCount": $quarantined_compile_error_count
}
EOF

echo "[mutation-trackb-assert] PASS: candidates=$candidate_count mapped=$mapped_span_count planned=$planned_mutant_count created=$created_mutant_count quarantined=$quarantined_compile_error_count classification=$classification"
