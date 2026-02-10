#!/usr/bin/env bash
set -euo pipefail

db_user="${POSTGRES_USER:-artifortress}"
db_name="${POSTGRES_DB:-artifortress}"

psql_exec() {
  docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d "$db_name" "$@"
}

required_tables=(
  schema_migrations
  tenants
  repos
  packages
  blobs
  package_versions
  artifact_entries
  audit_log
  outbox_events
)

for table_name in "${required_tables[@]}"; do
  exists="$(psql_exec -tAc "select to_regclass('public.${table_name}') is not null;")"
  normalized="$(echo "$exists" | tr -d '[:space:]')"

  if [ "$normalized" != "t" ]; then
    echo "Missing required table: $table_name"
    exit 1
  fi
done

applied_count="$(psql_exec -tAc "select count(*) from schema_migrations;")"
echo "DB smoke passed. Applied migrations: $(echo "$applied_count" | tr -d '[:space:]')"
