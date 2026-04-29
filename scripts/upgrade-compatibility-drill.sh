#!/usr/bin/env bash
set -euo pipefail

report_path="${REPORT_PATH:-docs/reports/upgrade-compatibility-drill-latest.md}"
db_user="${POSTGRES_USER:-kublai}"
base_db_prefix="${DRILL_DB_PREFIX:-kublai_upgrade_drill}"
release_tag="${KUBLAI_RELEASE_TAG:-<unset>}"
api_image_digest="${KUBLAI_API_IMAGE_DIGEST:-<unset>}"
worker_image_digest="${KUBLAI_WORKER_IMAGE_DIGEST:-<unset>}"
helm_chart_digest="${KUBLAI_HELM_CHART_DIGEST:-<unset>}"
release_sbom_path="${KUBLAI_RELEASE_SBOM_PATH:-<unset>}"
release_provenance_report="${KUBLAI_RELEASE_PROVENANCE_REPORT:-docs/reports/release-provenance-latest.md}"

baselines=(
  "0009_post_ga_search_read_model.sql|Phase 6 GA baseline"
  "0010_enterprise_identity_and_reliability.sql|Enterprise identity and reliability baseline"
  "0012_tenant_role_bindings_and_audit_correlation.sql|Tenant delegation and audit correlation baseline"
)

mkdir -p "$(dirname "$report_path")"

psql_admin() {
  docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d postgres "$@"
}

psql_db() {
  local db_name="$1"
  shift
  docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d "$db_name" "$@"
}

create_empty_db() {
  local db_name="$1"

  psql_admin <<SQL
select pg_terminate_backend(pid)
from pg_stat_activity
where datname = '${db_name}'
  and pid <> pg_backend_pid();

drop database if exists ${db_name};
create database ${db_name};
SQL
}

apply_migrations_until() {
  local db_name="$1"
  local stop_version="$2"

  psql_db "$db_name" <<'SQL'
create table if not exists schema_migrations (
  version text primary key,
  applied_at timestamptz not null default now()
);
SQL

  shopt -s nullglob
  local migration_files=(db/migrations/*.sql)

  for file in "${migration_files[@]}"; do
    local version
    version="$(basename "$file")"
    echo "[upgrade-compatibility-drill] Applying baseline migration ${version} to ${db_name}..."
    psql_db "$db_name" -f - < "$file"
    psql_db "$db_name" -c "insert into schema_migrations (version) values ('${version}');" >/dev/null

    if [ "$version" = "$stop_version" ]; then
      return 0
    fi
  done

  echo "Could not locate baseline migration ${stop_version}."
  exit 1
}

verify_head_schema() {
  local db_name="$1"
  local expected_head
  expected_head="$(basename "$(ls db/migrations/*.sql | sort | tail -n 1)")"
  local actual_head
  actual_head="$(psql_db "$db_name" -tAc "select version from schema_migrations order by version desc limit 1;" | tr -d '[:space:]')"

  if [ "$actual_head" != "$expected_head" ]; then
    echo "Expected head migration ${expected_head} in ${db_name}, got ${actual_head:-<empty>}."
    exit 1
  fi
}

results=()
overall_status="PASS"
start_epoch="$(date +%s)"
index=0

for baseline in "${baselines[@]}"; do
  index="$((index + 1))"
  version="${baseline%%|*}"
  label="${baseline#*|}"
  db_name="${base_db_prefix}_${index}"

  echo "[upgrade-compatibility-drill] Rehearsing upgrade path from ${version} (${label})..."

  if create_empty_db "$db_name" \
    && apply_migrations_until "$db_name" "$version" \
    && POSTGRES_DB="$db_name" ./scripts/db-migrate.sh >/dev/null \
    && POSTGRES_DB="$db_name" ./scripts/db-smoke.sh >/dev/null \
    && verify_head_schema "$db_name"; then
    results+=("PASS|${version}|${label}")
  else
    overall_status="FAIL"
    results+=("FAIL|${version}|${label}")
  fi

  create_empty_db "$db_name" >/dev/null
done

total_duration="$(( $(date +%s) - start_epoch ))"

{
  echo "# Upgrade Compatibility Drill Report"
  echo
  echo "Generated at: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "## Summary"
  echo
  echo "- overall status: ${overall_status}"
  echo "- total duration (seconds): ${total_duration}"
  echo
  echo "## Release Artifact Inputs"
  echo
  echo "- release tag: \`${release_tag}\`"
  echo "- API image digest: \`${api_image_digest}\`"
  echo "- worker image digest: \`${worker_image_digest}\`"
  echo "- Helm chart digest: \`${helm_chart_digest}\`"
  echo "- SBOM path: \`${release_sbom_path}\`"
  echo "- release provenance report: \`${release_provenance_report}\`"
  echo
  echo "## Rehearsed Paths"
  echo

  for result in "${results[@]}"; do
    status="${result%%|*}"
    remainder="${result#*|}"
    version="${remainder%%|*}"
    label="${remainder#*|}"
    echo "- ${status}: ${label} -> head from ${version}"
  done
} > "$report_path"

echo "[upgrade-compatibility-drill] Report written to $report_path"

if [ "$overall_status" != "PASS" ]; then
  echo "[upgrade-compatibility-drill] Drill failed."
  exit 1
fi

echo "[upgrade-compatibility-drill] Drill passed."
