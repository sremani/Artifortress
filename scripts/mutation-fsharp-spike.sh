#!/usr/bin/env bash
set -euo pipefail

report_path="${REPORT_PATH:-docs/reports/mutation-spike-fsharp-latest.md}"
log_path="${LOG_PATH:-/tmp/artifortress-mutation-fsharp-spike.log}"

mkdir -p "$(dirname "$report_path")"
mkdir -p "$(dirname "$log_path")"

echo "[mutation-spike] Running mutation-only test project baseline..."
dotnet test tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj --configuration Debug -v minimal >/tmp/artifortress-mutation-tests.log 2>&1

echo "[mutation-spike] Running Stryker feasibility probe on F# project..."
set +e
dotnet tool run dotnet-stryker -- \
  -tp tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj \
  -p src/Artifortress.Domain/Artifortress.Domain.fsproj \
  -m "src/Artifortress.Domain/Library.fs{8..102}" \
  -l Basic \
  -c 1 \
  -r ClearText \
  -r Json \
  -O artifacts/mutation/mut01-domain \
  --break-at 0 \
  --threshold-low 0 \
  --threshold-high 100 \
  --skip-version-check \
  --disable-bail \
  --break-on-initial-test-failure >"$log_path" 2>&1
stryker_exit=$?
set -e

status="blocked"
summary="Stryker reports F# mutation is not supported yet."

if [ "$stryker_exit" -eq 0 ]; then
  status="unexpected_success"
  summary="Stryker completed unexpectedly; re-check compatibility assumptions and captured report artifacts."
elif grep -Fq "Language not supported: Fsharp" "$log_path"; then
  status="blocked_known"
  summary="Confirmed blocker: Language not supported: Fsharp."
else
  status="failed_unknown"
  summary="Stryker failed for an unexpected reason. Inspect log output."
fi

{
  echo "# F# Mutation Feasibility Spike Report"
  echo
  echo "Generated at: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "## Inputs"
  echo
  echo "- test project: \`tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj\`"
  echo "- project under mutation: \`src/Artifortress.Domain/Artifortress.Domain.fsproj\`"
  echo "- mutate scope: \`src/Artifortress.Domain/Library.fs{8..102}\`"
  echo
  echo "## Outcome"
  echo
  echo "- status: ${status}"
  echo "- stryker exit code: ${stryker_exit}"
  echo "- summary: ${summary}"
  echo "- raw log: \`${log_path}\`"
  echo
  echo "## Next Action"
  echo
  if [ "$status" = "blocked_known" ]; then
    echo "- proceed with Track B upstream/fork engineering to add F# mutation capability."
  elif [ "$status" = "unexpected_success" ]; then
    echo "- expand spike scope and validate mutant quality before adopting gates."
  else
    echo "- resolve unexpected failure, then rerun \`make mutation-spike\`."
  fi
} >"$report_path"

echo "[mutation-spike] Report written to $report_path"
echo "[mutation-spike] Log written to $log_path"

if [ "$status" = "failed_unknown" ]; then
  exit 1
fi

exit 0
