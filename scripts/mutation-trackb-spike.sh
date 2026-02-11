#!/usr/bin/env bash
set -euo pipefail

workspace_dir="${STRYKER_WORKSPACE_DIR:-.cache/stryker-net}"
configuration="${STRYKER_BUILD_CONFIGURATION:-Release}"
report_path="${REPORT_PATH:-docs/reports/mutation-trackb-mut06-latest.md}"
result_json="${RESULT_JSON:-artifacts/mutation/mut06-trackb-latest.json}"
log_path="${LOG_PATH:-/tmp/artifortress-mutation-trackb-mut06.log}"
output_path="${OUTPUT_PATH:-artifacts/mutation/mut06-trackb}"

./scripts/mutation-trackb-bootstrap.sh
./scripts/mutation-trackb-build.sh

cli_dll="$(find "$workspace_dir/src/Stryker.CLI/Stryker.CLI/bin/$configuration" -type f -name Stryker.CLI.dll | head -n 1)"

if [ -z "$cli_dll" ]; then
  echo "[mutation-trackb-spike] Could not locate built Stryker CLI dll" >&2
  exit 1
fi

dotnet run --project tools/Artifortress.MutationTrack/Artifortress.MutationTrack.fsproj -- run \
  --stryker-cli "$cli_dll" \
  --output "$output_path" \
  --log "$log_path" \
  --report "$report_path" \
  --result-json "$result_json" \
  --allow-blocked true

echo "[mutation-trackb-spike] Report: $report_path"
echo "[mutation-trackb-spike] Result json: $result_json"
echo "[mutation-trackb-spike] Log: $log_path"
