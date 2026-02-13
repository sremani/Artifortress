#!/usr/bin/env bash
set -euo pipefail

history_csv_path="${HISTORY_CSV_PATH:-artifacts/mutation/mutation-native-score-history.csv}"
report_path="${REPORT_PATH:-docs/reports/mutation-native-burnin-latest.md}"
summary_path="${SUMMARY_PATH:-artifacts/ci/mutation-native-burnin-summary.txt}"
metrics_json_path="${METRICS_JSON_PATH:-artifacts/ci/mutation-native-burnin.json}"
required_streak="${REQUIRED_STREAK:-${MUTATION_NATIVE_REQUIRED_STREAK:-7}}"
min_score="${MIN_MUTATION_SCORE:-0}"
enforce_ready="${ENFORCE_BURNIN_READY:-false}"

if [ ! -f "$history_csv_path" ]; then
  echo "[mutation-fsharp-native-burnin] Missing history csv: $history_csv_path" >&2
  exit 1
fi

if ! [[ "$required_streak" =~ ^[0-9]+$ ]] || [ "$required_streak" -le 0 ]; then
  echo "[mutation-fsharp-native-burnin] REQUIRED_STREAK must be a positive integer (received '$required_streak')." >&2
  exit 1
fi

row_count="$(awk 'NR>1 {count++} END {print count+0}' "$history_csv_path")"
latest_timestamp="$(awk -F',' 'NR>1 {ts=$1} END {print ts}' "$history_csv_path")"

read -r passing_streak burnin_ready burnin_reason <<EOF_STATUS
$(awk -F',' -v min_score="$min_score" -v required="$required_streak" '
BEGIN {
  passing = 0
  total = 0
}
NR==1 { next }
{
  total++
  rows[total] = $0
}
END {
  pass_streak = 0
  ready = "false"
  reason = "insufficient_history"

  for (i = total; i >= 1; i--) {
    split(rows[i], f, ",")
    compile_err = f[6] + 0
    infra_err = f[7] + 0
    score = f[8] + 0.0
    threshold = f[10]
    is_pass = (compile_err == 0 && infra_err == 0 && threshold == "true" && score >= min_score)
    if (is_pass) {
      pass_streak++
    } else {
      break
    }
  }

  if (total < required) {
    ready = "false"
    reason = "insufficient_history"
  } else if (pass_streak < required) {
    ready = "false"
    reason = "streak_not_met"
  } else {
    ready = "true"
    reason = "ready"
  }

  printf "%d %s %s", pass_streak, ready, reason
}
' "$history_csv_path")
EOF_STATUS

mkdir -p "$(dirname "$report_path")"
mkdir -p "$(dirname "$summary_path")"
mkdir -p "$(dirname "$metrics_json_path")"

cat > "$report_path" <<EOF_REPORT
# Native F# Mutation Burn-in Readiness

Generated at: $(date -u +"%Y-%m-%dT%H:%M:%SZ")

## Inputs

- history csv: $history_csv_path
- required passing streak: $required_streak
- minimum score floor: $min_score

## Current Status

- total history rows: $row_count
- current passing streak: $passing_streak
- latest run timestamp: ${latest_timestamp:-n/a}
- burn-in ready: $burnin_ready
- reason: $burnin_reason

Readiness rule for each run in streak:
- compile_error_count == 0
- infrastructure_error_count == 0
- threshold_met == true
- score_percent >= MIN_MUTATION_SCORE

## Recent Runs (latest 20)

| timestamp | selected | tested | killed | survived | compile_error | infra_error | score | min_score | threshold_met |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
EOF_REPORT

tail -n 20 "$history_csv_path" | tail -n +2 | while IFS=',' read -r ts sel tst kil sur comp inf score min th; do
  printf '| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |\n' \
    "$ts" "$sel" "$tst" "$kil" "$sur" "$comp" "$inf" "$score" "$min" "$th" >> "$report_path"
done

cat > "$summary_path" <<EOF_SUMMARY
history_csv_path=$history_csv_path
row_count=$row_count
required_streak=$required_streak
min_score=$min_score
passing_streak=$passing_streak
burnin_ready=$burnin_ready
burnin_reason=$burnin_reason
latest_timestamp=${latest_timestamp:-}
report_path=$report_path
EOF_SUMMARY

cat > "$metrics_json_path" <<EOF_METRICS
{
  "historyCsvPath": "$history_csv_path",
  "rowCount": $row_count,
  "requiredStreak": $required_streak,
  "minScore": $min_score,
  "passingStreak": $passing_streak,
  "burnInReady": $burnin_ready,
  "burnInReason": "$burnin_reason",
  "latestTimestamp": "${latest_timestamp:-}"
}
EOF_METRICS

if [ "$enforce_ready" = "true" ] && [ "$burnin_ready" != "true" ]; then
  echo "[mutation-fsharp-native-burnin] Burn-in not ready: passing_streak=$passing_streak required=$required_streak reason=$burnin_reason" >&2
  exit 1
fi

echo "[mutation-fsharp-native-burnin] PASS: ready=$burnin_ready passing_streak=$passing_streak/$required_streak reason=$burnin_reason"
