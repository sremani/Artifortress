#!/usr/bin/env bash
set -euo pipefail

repo_url="${STRYKER_REPO_URL:-https://github.com/stryker-mutator/stryker-net.git}"
workspace_dir="${STRYKER_WORKSPACE_DIR:-.cache/stryker-net}"
base_ref="${STRYKER_BASE_REF:-c3e2701e}"
branch_name="${STRYKER_BRANCH_NAME:-artifortress/mutation-trackb-experimental}"
patch_dir="${STRYKER_PATCH_DIR:-patches/stryker-net}"

if [ ! -d "$patch_dir" ]; then
  echo "[mutation-trackb-bootstrap] Patch directory not found: $patch_dir" >&2
  exit 1
fi

patch_files=()
while IFS= read -r patch_file; do
  patch_files+=("$patch_file")
done < <(find "$patch_dir" -maxdepth 1 -type f -name '*.patch' | sort)

if [ ${#patch_files[@]} -eq 0 ]; then
  echo "[mutation-trackb-bootstrap] No patch files found in: $patch_dir" >&2
  exit 1
fi

mkdir -p "$(dirname "$workspace_dir")"

if [ ! -d "$workspace_dir/.git" ]; then
  echo "[mutation-trackb-bootstrap] Cloning Stryker.NET to $workspace_dir"
  git clone "$repo_url" "$workspace_dir"
fi

(
  cd "$workspace_dir"
  git fetch origin --tags --prune
  git checkout -B "$branch_name" "$base_ref"
  git reset --hard "$base_ref" >/dev/null
  git clean -fd >/dev/null
)

for patch_file in "${patch_files[@]}"; do
  patch_abs="$(cd "$(dirname "$patch_file")" && pwd)/$(basename "$patch_file")"

  (
    cd "$workspace_dir"

    if git apply --reverse --check "$patch_abs" >/dev/null 2>&1; then
      echo "[mutation-trackb-bootstrap] Patch already applied: $(basename "$patch_abs")"
    else
      git apply --check "$patch_abs"
      git apply "$patch_abs"
      echo "[mutation-trackb-bootstrap] Patch applied: $(basename "$patch_abs")"
    fi
  )
done

echo "[mutation-trackb-bootstrap] Ready workspace: $workspace_dir"
