#!/usr/bin/env bash
set -euo pipefail

chart_path="${HELM_CLOUD_EXAMPLES_CHART:-deploy/helm/kublai}"
examples=(
  "values-lke-preprod.example.yaml"
  "values-lke-production.example.yaml"
)

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

require_command helm

for example in "${examples[@]}"; do
  values_path="${chart_path}/${example}"
  release_name="kublai-${example%.example.yaml}"
  rendered="$(mktemp)"

  echo "[helm-cloud-examples] lint ${values_path}"
  helm lint "$chart_path" --values "$values_path"

  echo "[helm-cloud-examples] template ${values_path}"
  helm template "$release_name" "$chart_path" \
    --namespace kublai-example \
    --values "$values_path" >"$rendered"

  if grep -q "kind: Secret" "$rendered"; then
    echo "Expected ${values_path} to use an externally managed Secret." >&2
    exit 1
  fi

  expected_secret="$(sed -n 's/^[[:space:]]*existingSecretName:[[:space:]]*//p' "$values_path" | head -n 1)"
  if [ -z "$expected_secret" ]; then
    echo "Missing secrets.existingSecretName in ${values_path}." >&2
    exit 1
  fi

  if ! grep -q "name: ${expected_secret}" "$rendered"; then
    echo "Rendered manifests do not reference ${expected_secret}." >&2
    exit 1
  fi

  rm -f "$rendered"
done

echo "[helm-cloud-examples] PASS"
