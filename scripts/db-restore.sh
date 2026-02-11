#!/usr/bin/env bash
set -euo pipefail

db_user="${POSTGRES_USER:-artifortress}"
target_db="${TARGET_DB:-${POSTGRES_DB:-artifortress}}"
restore_path="${RESTORE_PATH:-}"

if [[ ! "$target_db" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
  echo "TARGET_DB must match ^[A-Za-z_][A-Za-z0-9_]*$"
  exit 1
fi

if [ -z "$restore_path" ]; then
  echo "RESTORE_PATH is required."
  echo "Example: RESTORE_PATH=/tmp/artifortress-backup.sql make db-restore"
  exit 1
fi

if [ ! -f "$restore_path" ]; then
  echo "Restore file does not exist: $restore_path"
  exit 1
fi

echo "Restoring Postgres database '$target_db' from '$restore_path'..."

docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d postgres <<SQL
select pg_terminate_backend(pid)
from pg_stat_activity
where datname = '${target_db}'
  and pid <> pg_backend_pid();

drop database if exists ${target_db};
create database ${target_db};
SQL

docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d "$target_db" -f - < "$restore_path"

echo "Restore completed for database '$target_db'."
