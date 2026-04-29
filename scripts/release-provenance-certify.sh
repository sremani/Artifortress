#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
usage:
  release-provenance-certify.sh <tag> [asset-dir]

example:
  release-provenance-certify.sh v1.0.0-rc.1

Downloads GitHub Release assets for a tag, verifies checksums, verifies cosign
blob signatures/certificates, verifies OCI image signatures, and writes a
release provenance evidence report.
EOF
}

if [[ $# -lt 1 || $# -gt 2 ]]; then
  usage
  exit 1
fi

TAG="$1"
VERSION="${TAG#v}"
ROOT_DIR="$(git rev-parse --show-toplevel)"
ASSET_DIR="${2:-${ROOT_DIR}/artifacts/release-provenance/${TAG}}"
REPORT_PATH="${RELEASE_PROVENANCE_REPORT:-${ROOT_DIR}/docs/reports/release-provenance-latest.md}"
REPORT_DIR="$(dirname "${REPORT_PATH}")"
STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
START_SECONDS="$(date -u +%s)"
STATUS="PASS"
STEP_ROWS=()

if [[ "${TAG}" != v* ]]; then
  echo "tag must start with 'v': ${TAG}"
  exit 1
fi

if ! [[ "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]]; then
  echo "tag must be SemVer-like, for example v1.0.0 or v1.0.0-rc.1: ${TAG}"
  exit 1
fi

mkdir -p "${ASSET_DIR}" "${REPORT_DIR}"

slugify() {
  printf '%s' "$1" | tr -c '[:alnum:]' '-'
}

append_step() {
  local label="$1"
  local command="$2"
  local status="$3"
  local duration="$4"
  local log_path="$5"

  STEP_ROWS+=("| ${label} | \`${command}\` | ${status} | ${duration} | \`${log_path}\` |")
}

write_report() {
  local ended_at
  local ended_seconds
  local duration_seconds
  local commit
  local tag_subject
  local api_ref
  local worker_ref

  ended_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  ended_seconds="$(date -u +%s)"
  duration_seconds=$((ended_seconds - START_SECONDS))
  commit="$(git rev-list -n 1 "${TAG}" 2>/dev/null || echo unknown)"
  tag_subject="$(git for-each-ref "refs/tags/${TAG}" --format='%(subject)' 2>/dev/null || echo unknown)"
  api_ref="not verified"
  worker_ref="not verified"

  if [[ -f "${ASSET_DIR}/OCI-IMAGES.txt" ]]; then
    api_ref="$(sed -n 's/^api image: //p' "${ASSET_DIR}/OCI-IMAGES.txt" | head -n 1)"
    worker_ref="$(sed -n 's/^worker image: //p' "${ASSET_DIR}/OCI-IMAGES.txt" | head -n 1)"
    api_ref="${api_ref:-not found}"
    worker_ref="${worker_ref:-not found}"
  fi

  {
    echo "# Release Provenance Evidence"
    echo
    echo "Generated at: ${ended_at}"
    echo
    echo "## Summary"
    echo
    echo "- overall status: ${STATUS}"
    echo "- tag: ${TAG}"
    echo "- version: ${VERSION}"
    echo "- tag subject: ${tag_subject}"
    echo "- tag commit: ${commit}"
    echo "- asset directory: \`${ASSET_DIR}\`"
    echo "- started at: ${STARTED_AT}"
    echo "- ended at: ${ended_at}"
    echo "- total duration seconds: ${duration_seconds}"
    echo
    echo "## Verified OCI Images"
    echo
    echo "- API: \`${api_ref}\`"
    echo "- Worker: \`${worker_ref}\`"
    echo
    echo "## Verification Steps"
    echo
    echo "| Step | Command | Status | Duration Seconds | Log |"
    echo "|---|---|---:|---:|---|"
    printf '%s\n' "${STEP_ROWS[@]}"
    echo
    echo "## Required Release Assets"
    echo
    for asset in "${REQUIRED_ASSETS[@]}"; do
      echo "- \`${asset}\`"
    done
  } > "${REPORT_PATH}"
}

on_exit() {
  local exit_code=$?
  if [[ "${exit_code}" -ne 0 ]]; then
    STATUS="FAIL"
  fi
  write_report
  echo "[release-provenance] Report written to ${REPORT_PATH}"
  exit "${exit_code}"
}

trap on_exit EXIT

run_step() {
  local label="$1"
  local command="$2"
  local started
  local ended
  local duration
  local log_file
  local log_rel

  log_file="${ASSET_DIR}/$(slugify "${label}").log"
  log_rel="${log_file#"${ROOT_DIR}"/}"
  echo "[release-provenance] Running: ${label}"
  started="$(date -u +%s)"

  if /bin/bash -lc "${command}" > "${log_file}" 2>&1; then
    ended="$(date -u +%s)"
    duration=$((ended - started))
    append_step "${label}" "${command}" "PASS" "${duration}" "${log_rel}"
    echo "[release-provenance] Passed: ${label}"
  else
    ended="$(date -u +%s)"
    duration=$((ended - started))
    append_step "${label}" "${command}" "FAIL" "${duration}" "${log_rel}"
    STATUS="FAIL"
    echo "[release-provenance] Failed: ${label}"
    return 1
  fi
}

REQUIRED_ASSETS=(
  "kublai-api-linux-x64.tar.gz"
  "kublai-api-linux-x64.tar.gz.sig"
  "kublai-api-linux-x64.tar.gz.crt"
  "kublai-worker-linux-x64.tar.gz"
  "kublai-worker-linux-x64.tar.gz.sig"
  "kublai-worker-linux-x64.tar.gz.crt"
  "kublai-api.sbom.cdx.json"
  "kublai-api.sbom.cdx.json.sig"
  "kublai-api.sbom.cdx.json.crt"
  "kublai-worker.sbom.cdx.json"
  "kublai-worker.sbom.cdx.json.sig"
  "kublai-worker.sbom.cdx.json.crt"
  "kublai-api-image.sbom.cdx.json"
  "kublai-api-image.sbom.cdx.json.sig"
  "kublai-api-image.sbom.cdx.json.crt"
  "kublai-worker-image.sbom.cdx.json"
  "kublai-worker-image.sbom.cdx.json.sig"
  "kublai-worker-image.sbom.cdx.json.crt"
  "kublai-helm-chart.sbom.cdx.json"
  "kublai-helm-chart.sbom.cdx.json.sig"
  "kublai-helm-chart.sbom.cdx.json.crt"
  "kublai-${VERSION}.tgz"
  "kublai-${VERSION}.tgz.sig"
  "kublai-${VERSION}.tgz.crt"
  "OCI-IMAGES.txt"
  "SHA256SUMS"
)

VERIFY="${ROOT_DIR}/scripts/verify-release-artifacts.sh"

run_step "fetch release tag" "cd '${ROOT_DIR}' && git fetch origin 'refs/tags/${TAG}:refs/tags/${TAG}'"
run_step "verify signed git tag" "cd '${ROOT_DIR}' && git verify-tag '${TAG}'"
run_step "check remote tag" "cd '${ROOT_DIR}' && git ls-remote --exit-code --tags origin 'refs/tags/${TAG}'"
run_step "check GitHub release" "cd '${ROOT_DIR}' && gh release view '${TAG}'"
run_step "download release assets" "cd '${ROOT_DIR}' && gh release download '${TAG}' --dir '${ASSET_DIR}' --clobber"

missing_assets=()
for asset in "${REQUIRED_ASSETS[@]}"; do
  if [[ ! -f "${ASSET_DIR}/${asset}" ]]; then
    missing_assets+=("${asset}")
  fi
done

if [[ "${#missing_assets[@]}" -ne 0 ]]; then
  printf 'missing required release assets:\n' >&2
  printf '  %s\n' "${missing_assets[@]}" >&2
  exit 1
fi

run_step "verify checksums" "cd '${ASSET_DIR}' && '${VERIFY}' checksum SHA256SUMS"

BLOB_ASSETS=(
  "kublai-api-linux-x64.tar.gz"
  "kublai-worker-linux-x64.tar.gz"
  "kublai-api.sbom.cdx.json"
  "kublai-worker.sbom.cdx.json"
  "kublai-api-image.sbom.cdx.json"
  "kublai-worker-image.sbom.cdx.json"
  "kublai-helm-chart.sbom.cdx.json"
  "kublai-${VERSION}.tgz"
)

for asset in "${BLOB_ASSETS[@]}"; do
  run_step "verify ${asset} signature" \
    "cd '${ASSET_DIR}' && '${VERIFY}' blob '${asset}' '${asset}.sig' '${asset}.crt'"
done

api_ref="$(sed -n 's/^api image: //p' "${ASSET_DIR}/OCI-IMAGES.txt" | head -n 1)"
worker_ref="$(sed -n 's/^worker image: //p' "${ASSET_DIR}/OCI-IMAGES.txt" | head -n 1)"

if [[ -z "${api_ref}" || -z "${worker_ref}" ]]; then
  echo "OCI-IMAGES.txt did not contain both API and worker digest references"
  exit 1
fi

run_step "verify API image signature" "cd '${ROOT_DIR}' && '${VERIFY}' image '${api_ref}'"
run_step "verify worker image signature" "cd '${ROOT_DIR}' && '${VERIFY}' image '${worker_ref}'"
