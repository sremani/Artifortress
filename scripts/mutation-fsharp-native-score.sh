#!/usr/bin/env bash
set -euo pipefail

result_json="${RESULT_JSON:-artifacts/mutation/mutation-native-fsharp-latest.json}"
report_path="${REPORT_PATH:-docs/reports/mutation-native-score-latest.md}"
summary_path="${SUMMARY_PATH:-artifacts/ci/mutation-native-score-summary.txt}"
metrics_json_path="${METRICS_JSON_PATH:-artifacts/ci/mutation-native-score.json}"
min_score="${MIN_MUTATION_SCORE:-0}"

if [ ! -f "$result_json" ]; then
  echo "[mutation-fsharp-native-score] Missing result json: $result_json" >&2
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
tested_count=$((killed_count + survived_count))

score_percent="$(awk -v killed="$killed_count" -v survived="$survived_count" 'BEGIN { tested=killed+survived; if (tested <= 0) { printf "0.00" } else { printf "%.2f", (killed*100.0)/tested } }')"
threshold_met="$(awk -v score="$score_percent" -v min="$min_score" 'BEGIN { if ((score + 0.0) >= (min + 0.0)) print "true"; else print "false" }')"

mkdir -p "$(dirname "$report_path")"
mkdir -p "$(dirname "$summary_path")"
mkdir -p "$(dirname "$metrics_json_path")"

cat > "$report_path" <<EOF_REPORT
# Native F# Mutation Score Report

Generated at: $(date -u +"%Y-%m-%dT%H:%M:%SZ")

## Inputs

- result json: $result_json
- minimum score threshold: $min_score

## Counts

- selected mutants: $selected_count
- tested mutants (killed + survived): $tested_count
- killed: $killed_count
- survived: $survived_count
- compile errors: $compile_error_count
- infrastructure errors: $infra_error_count

## Score

- mutation score (%): $score_percent
- threshold met: $threshold_met
EOF_REPORT

cat > "$summary_path" <<EOF_SUMMARY
selected_count=$selected_count
tested_count=$tested_count
killed_count=$killed_count
survived_count=$survived_count
compile_error_count=$compile_error_count
infrastructure_error_count=$infra_error_count
score_percent=$score_percent
min_score=$min_score
threshold_met=$threshold_met
report_path=$report_path
result_json=$result_json
EOF_SUMMARY

cat > "$metrics_json_path" <<EOF_METRICS
{
  "selectedMutantCount": $selected_count,
  "testedMutantCount": $tested_count,
  "killedCount": $killed_count,
  "survivedCount": $survived_count,
  "compileErrorCount": $compile_error_count,
  "infrastructureErrorCount": $infra_error_count,
  "scorePercent": $score_percent,
  "minScore": $min_score,
  "thresholdMet": $threshold_met
}
EOF_METRICS

if [ "$tested_count" -le 0 ]; then
  echo "[mutation-fsharp-native-score] No tested mutants were produced." >&2
  exit 1
fi

if [ "$threshold_met" != "true" ]; then
  echo "[mutation-fsharp-native-score] Score threshold not met: score=$score_percent min=$min_score" >&2
  exit 1
fi

echo "[mutation-fsharp-native-score] PASS: score=$score_percent tested=$tested_count killed=$killed_count survived=$survived_count"
