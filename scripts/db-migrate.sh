#!/usr/bin/env bash
set -euo pipefail

db_user="${POSTGRES_USER:-artifortress}"
db_name="${POSTGRES_DB:-artifortress}"

psql_exec() {
  docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d "$db_name" "$@"
}

psql_exec <<'SQL'
create table if not exists schema_migrations (
  version text primary key,
  applied_at timestamptz not null default now()
);
SQL

shopt -s nullglob
migration_files=(db/migrations/*.sql)

if [ "${#migration_files[@]}" -eq 0 ]; then
  echo "No migration files found in db/migrations."
  exit 0
fi

for file in "${migration_files[@]}"; do
  version="$(basename "$file")"
  version_sql="${version//\'/\'\'}"
  applied="$(psql_exec -tAc "select 1 from schema_migrations where version = '${version_sql}' limit 1;")"

  if [ "$applied" = "1" ]; then
    echo "Skipping $version (already applied)."
    continue
  fi

  echo "Applying $version..."
  psql_exec -f - < "$file"
  psql_exec -c "insert into schema_migrations (version) values ('${version_sql}');"
done

echo "Migrations are up to date."
