#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "usage: $0 <artifact> <signature> <certificate> [identity-regexp]"
  exit 1
fi

artifact_path="$1"
signature_path="$2"
certificate_path="$3"
identity_regexp="${4:-https://github.com/.*/Artifortress/.*}"

if ! command -v cosign >/dev/null 2>&1; then
  echo "cosign is required"
  exit 1
fi

cosign verify-blob \
  --signature "${signature_path}" \
  --certificate "${certificate_path}" \
  --certificate-identity-regexp "${identity_regexp}" \
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com" \
  "${artifact_path}"
