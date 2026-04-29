#!/usr/bin/env bash
set -euo pipefail

doc_path="${OFFLINE_INSTALL_DOC:-docs/81-airgapped-offline-install-plan.md}"
manifest_path="${OFFLINE_RELEASE_MANIFEST:-deploy/offline/release-manifest.example.env}"

required_headings=(
  "## Offline Bundle Contents"
  "## Mirror Procedure"
  "## Offline Verification"
  "## Offline Install Procedure"
  "## Offline Upgrade Procedure"
  "## Unsupported Assumptions"
)

required_manifest_keys=(
  "KUBLAI_RELEASE_TAG"
  "KUBLAI_CHART_PACKAGE"
  "KUBLAI_CHART_SIGNATURE"
  "KUBLAI_CHART_CERTIFICATE"
  "KUBLAI_API_IMAGE"
  "KUBLAI_WORKER_IMAGE"
  "KUBLAI_SHA256SUMS"
  "KUBLAI_API_SBOM"
  "KUBLAI_WORKER_SBOM"
  "KUBLAI_API_IMAGE_SBOM"
  "KUBLAI_WORKER_IMAGE_SBOM"
  "KUBLAI_HELM_SBOM"
  "KUBLAI_PROVENANCE_REPORT"
  "KUBLAI_OFFLINE_REGISTRY"
)

if [ ! -f "$doc_path" ]; then
  echo "[offline-install-plan-validate] Missing offline install doc: ${doc_path}" >&2
  exit 1
fi

if [ ! -f "$manifest_path" ]; then
  echo "[offline-install-plan-validate] Missing offline release manifest: ${manifest_path}" >&2
  exit 1
fi

for heading in "${required_headings[@]}"; do
  if ! grep -q "^${heading}$" "$doc_path"; then
    echo "[offline-install-plan-validate] Missing heading in ${doc_path}: ${heading}" >&2
    exit 1
  fi
done

for key in "${required_manifest_keys[@]}"; do
  if ! grep -q "^${key}=" "$manifest_path"; then
    echo "[offline-install-plan-validate] Missing key in ${manifest_path}: ${key}" >&2
    exit 1
  fi
done

echo "[offline-install-plan-validate] PASS"
