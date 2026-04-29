#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
usage:
  verify-release-artifacts.sh blob <artifact> <signature> <certificate> [identity-regexp]
  verify-release-artifacts.sh image <image-ref> [identity-regexp]
  verify-release-artifacts.sh checksum <SHA256SUMS>

legacy:
  verify-release-artifacts.sh <artifact> <signature> <certificate> [identity-regexp]
EOF
}

require_cosign() {
  if ! command -v cosign >/dev/null 2>&1; then
    echo "cosign is required"
    exit 1
  fi
}

identity_regexp_default='https://github.com/.*/Kublai/.*'
issuer='https://token.actions.githubusercontent.com'

if [[ $# -lt 1 ]]; then
  usage
  exit 1
fi

mode="$1"

case "${mode}" in
  blob)
    if [[ $# -lt 4 ]]; then
      usage
      exit 1
    fi

    require_cosign
    artifact_path="$2"
    signature_path="$3"
    certificate_path="$4"
    identity_regexp="${5:-${identity_regexp_default}}"

    cosign verify-blob \
      --signature "${signature_path}" \
      --certificate "${certificate_path}" \
      --certificate-identity-regexp "${identity_regexp}" \
      --certificate-oidc-issuer "${issuer}" \
      "${artifact_path}"
    ;;

  image)
    if [[ $# -lt 2 ]]; then
      usage
      exit 1
    fi

    require_cosign
    image_ref="$2"
    identity_regexp="${3:-${identity_regexp_default}}"

    cosign verify \
      --certificate-identity-regexp "${identity_regexp}" \
      --certificate-oidc-issuer "${issuer}" \
      "${image_ref}"
    ;;

  checksum)
    if [[ $# -ne 2 ]]; then
      usage
      exit 1
    fi

    sha256sum --check "$2"
    ;;

  *)
    if [[ $# -lt 3 ]]; then
      usage
      exit 1
    fi

    require_cosign
    artifact_path="$1"
    signature_path="$2"
    certificate_path="$3"
    identity_regexp="${4:-${identity_regexp_default}}"

    cosign verify-blob \
      --signature "${signature_path}" \
      --certificate "${certificate_path}" \
      --certificate-identity-regexp "${identity_regexp}" \
      --certificate-oidc-issuer "${issuer}" \
      "${artifact_path}"
    ;;
esac
