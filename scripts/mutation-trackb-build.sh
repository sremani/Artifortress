#!/usr/bin/env bash
set -euo pipefail

workspace_dir="${STRYKER_WORKSPACE_DIR:-.cache/stryker-net}"
configuration="${STRYKER_BUILD_CONFIGURATION:-Release}"

if [ ! -d "$workspace_dir/.git" ]; then
  echo "[mutation-trackb-build] Missing workspace at $workspace_dir" >&2
  echo "Run ./scripts/mutation-trackb-bootstrap.sh first." >&2
  exit 1
fi

(
  cd "$workspace_dir"
  dotnet build src/Stryker.CLI/Stryker.CLI/Stryker.CLI.csproj -c "$configuration" -v minimal
)

cli_dll="$(find "$workspace_dir/src/Stryker.CLI/Stryker.CLI/bin/$configuration" -type f -name Stryker.CLI.dll | head -n 1)"

if [ -z "$cli_dll" ]; then
  echo "[mutation-trackb-build] Failed to locate built Stryker.CLI.dll" >&2
  exit 1
fi

echo "[mutation-trackb-build] Built CLI: $cli_dll"
