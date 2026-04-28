#!/usr/bin/env bash
set -euo pipefail

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
bundle_root="${SUPPORT_BUNDLE_DIR:-/tmp/artifortress-support-bundle-${timestamp}}"
archive_path="${SUPPORT_BUNDLE_ARCHIVE:-${bundle_root}.tar.gz}"

mkdir -p "$bundle_root"
mkdir -p "$bundle_root/reports"

write_command() {
  local output_path="$1"
  shift

  {
    printf '$'
    printf ' %q' "$@"
    printf '\n\n'
    "$@" 2>&1 || true
  } > "$output_path"
}

write_http_snapshot() {
  local label="$1"
  local url="$2"
  local output_path="$3"
  shift 3

  {
    echo "# ${label}"
    echo
    echo "URL: ${url}"
    echo
    curl -sS --max-time "${SUPPORT_BUNDLE_CURL_TIMEOUT_SECONDS:-10}" "$@" "$url" 2>&1 || true
  } > "$output_path"
}

cat > "$bundle_root/README.md" <<EOF
# Artifortress Support Bundle

Generated at: ${timestamp}

This bundle is intended for support triage. It avoids collecting raw secret
values. Review the bundle before sharing it outside your organization.
EOF

cat > "$bundle_root/metadata.md" <<EOF
# Metadata

- generated at: ${timestamp}
- hostname: $(hostname 2>/dev/null || echo unknown)
- uname: $(uname -a 2>/dev/null || echo unknown)
- git commit: $(git rev-parse HEAD 2>/dev/null || echo unknown)
- git branch: $(git branch --show-current 2>/dev/null || echo unknown)
- .NET SDK: $(dotnet --version 2>/dev/null || echo unknown)
- .NET runtimes:

\`\`\`text
$(dotnet --list-runtimes 2>/dev/null || echo unknown)
\`\`\`
EOF

known_config_keys=(
  "ConnectionStrings__Postgres"
  "Auth__BootstrapToken"
  "Auth__Oidc__Enabled"
  "Auth__Oidc__Issuer"
  "Auth__Oidc__Audience"
  "Auth__Oidc__Hs256SharedSecret"
  "Auth__Oidc__JwksJson"
  "Auth__Oidc__JwksUrl"
  "Auth__Saml__Enabled"
  "Auth__Saml__IdpMetadataUrl"
  "Auth__Saml__IdpMetadataXml"
  "Auth__Saml__ExpectedIssuer"
  "Auth__Saml__ServiceProviderEntityId"
  "ObjectStorage__Endpoint"
  "ObjectStorage__AccessKey"
  "ObjectStorage__SecretKey"
  "ObjectStorage__Bucket"
  "ASPNETCORE_ENVIRONMENT"
  "ASPNETCORE_URLS"
  "Worker__PollSeconds"
  "Worker__BatchSize"
  "Worker__SearchJobMaxAttempts"
  "Worker__SearchJobLeaseSeconds"
)

{
  echo "# Configuration Shape"
  echo
  echo "Values are intentionally not emitted. This table only records whether a known key is present in the bundle collection environment."
  echo
  echo "| Key | Present |"
  echo "|---|---:|"
  for key in "${known_config_keys[@]}"; do
    if [ -n "${!key+x}" ]; then
      echo "| \`${key}\` | yes |"
    else
      echo "| \`${key}\` | no |"
    fi
  done
} > "$bundle_root/config-shape.md"

write_command "$bundle_root/git-status.txt" git status --short --branch
write_command "$bundle_root/docker-ps.txt" docker ps
write_command "$bundle_root/docker-compose-ps.txt" docker compose ps

if [ -n "${API_URL:-}" ]; then
  write_http_snapshot "Liveness" "${API_URL%/}/health/live" "$bundle_root/health-live.txt"
  write_http_snapshot "Readiness" "${API_URL%/}/health/ready" "$bundle_root/health-ready.txt"

  if [ -n "${ADMIN_TOKEN:-}" ]; then
    write_http_snapshot \
      "Admin Ops Summary" \
      "${API_URL%/}/v1/admin/ops/summary" \
      "$bundle_root/admin-ops-summary.txt" \
      -H "Authorization: Bearer ${ADMIN_TOKEN}"
  else
    cat > "$bundle_root/admin-ops-summary.txt" <<EOF
# Admin Ops Summary

Skipped because ADMIN_TOKEN was not provided.
EOF
  fi
else
  cat > "$bundle_root/health-live.txt" <<EOF
# Liveness

Skipped because API_URL was not provided.
EOF
  cat > "$bundle_root/health-ready.txt" <<EOF
# Readiness

Skipped because API_URL was not provided.
EOF
  cat > "$bundle_root/admin-ops-summary.txt" <<EOF
# Admin Ops Summary

Skipped because API_URL was not provided.
EOF
fi

for report in \
  docs/reports/enterprise-verification-latest.md \
  docs/reports/phase2-load-baseline-latest.md \
  docs/reports/phase6-rto-rpo-drill-latest.md \
  docs/reports/upgrade-compatibility-drill-latest.md \
  docs/reports/reliability-drill-latest.md \
  docs/reports/search-soak-drill-latest.md \
  docs/reports/performance-workflow-baseline-latest.md \
  docs/reports/performance-soak-latest.md
do
  if [ -f "$report" ]; then
    cp "$report" "$bundle_root/reports/"
  fi
done

tar -czf "$archive_path" -C "$(dirname "$bundle_root")" "$(basename "$bundle_root")"

cat <<EOF
Support bundle directory: ${bundle_root}
Support bundle archive: ${archive_path}
Review the archive before sharing it outside your organization.
EOF
