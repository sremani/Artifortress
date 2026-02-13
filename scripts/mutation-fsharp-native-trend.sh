#!/usr/bin/env bash
set -euo pipefail

score_summary_path="${SCORE_SUMMARY_PATH:-artifacts/ci/mutation-native-score-summary.txt}"
history_csv_path="${HISTORY_CSV_PATH:-artifacts/mutation/mutation-native-score-history.csv}"
report_path="${REPORT_PATH:-docs/reports/mutation-native-score-history-latest.md}"
summary_path="${SUMMARY_PATH:-artifacts/ci/mutation-native-trend-summary.txt}"
metrics_json_path="${METRICS_JSON_PATH:-artifacts/ci/mutation-native-trend.json}"

if [ ! -f "$score_summary_path" ]; then
  echo "[mutation-fsharp-native-trend] Missing score summary: $score_summary_path" >&2
  exit 1
fi

extract_summary_value() {
  local key="$1"
  local file="$2"
  sed -n -E "s/^${key}=//p" "$file" | head -n 1
}

timestamp="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
selected_count="$(extract_summary_value "selected_count" "$score_summary_path")"
tested_count="$(extract_summary_value "tested_count" "$score_summary_path")"
killed_count="$(extract_summary_value "killed_count" "$score_summary_path")"
survived_count="$(extract_summary_value "survived_count" "$score_summary_path")"
compile_error_count="$(extract_summary_value "compile_error_count" "$score_summary_path")"
infra_error_count="$(extract_summary_value "infrastructure_error_count" "$score_summary_path")"
score_percent="$(extract_summary_value "score_percent" "$score_summary_path")"
min_score="$(extract_summary_value "min_score" "$score_summary_path")"
threshold_met="$(extract_summary_value "threshold_met" "$score_summary_path")"

for required in selected_count tested_count killed_count survived_count compile_error_count infra_error_count score_percent min_score threshold_met; do
  if [ -z "${!required:-}" ]; then
    echo "[mutation-fsharp-native-trend] Missing required value '$required' in $score_summary_path" >&2
    exit 1
  fi
done

mkdir -p "$(dirname "$history_csv_path")"
mkdir -p "$(dirname "$report_path")"
mkdir -p "$(dirname "$summary_path")"
mkdir -p "$(dirname "$metrics_json_path")"

if [ ! -f "$history_csv_path" ]; then
  cat > "$history_csv_path" <<EOF_HEADER
timestamp,selected_count,tested_count,killed_count,survived_count,compile_error_count,infrastructure_error_count,score_percent,min_score,threshold_met
EOF_HEADER
fi

printf "%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n" \
  "$timestamp" \
  "$selected_count" \
  "$tested_count" \
  "$killed_count" \
  "$survived_count" \
  "$compile_error_count" \
  "$infra_error_count" \
  "$score_percent" \
  "$min_score" \
  "$threshold_met" >> "$history_csv_path"

row_count="$(awk 'NR>1 {count++} END {print count+0}' "$history_csv_path")"

cat > "$report_path" <<EOF_REPORT
# Native F# Mutation Score Trend

Generated at: $timestamp

- history rows: $row_count
- source summary: $score_summary_path

## Recent Runs (latest 20)

| timestamp | selected | tested | killed | survived | compile_error | infra_error | score | min_score | threshold_met |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
EOF_REPORT

tail -n 20 "$history_csv_path" | tail -n +2 | while IFS=',' read -r ts sel tst kil sur comp inf score min th; do
  printf '| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |\n' \
    "$ts" "$sel" "$tst" "$kil" "$sur" "$comp" "$inf" "$score" "$min" "$th" >> "$report_path"
done

cat > "$summary_path" <<EOF_SUMMARY
timestamp=$timestamp
row_count=$row_count
history_csv_path=$history_csv_path
report_path=$report_path
source_summary=$score_summary_path
EOF_SUMMARY

cat > "$metrics_json_path" <<EOF_METRICS
{
  "timestamp": "$timestamp",
  "rowCount": $row_count,
  "selectedCount": $selected_count,
  "testedCount": $tested_count,
  "killedCount": $killed_count,
  "survivedCount": $survived_count,
  "compileErrorCount": $compile_error_count,
  "infrastructureErrorCount": $infra_error_count,
  "scorePercent": $score_percent,
  "minScore": $min_score,
  "thresholdMet": $threshold_met
}
EOF_METRICS

echo "[mutation-fsharp-native-trend] PASS: rows=$row_count score=$score_percent threshold_met=$threshold_met"
