#!/usr/bin/env bash
set -euo pipefail

db_user="${POSTGRES_USER:-artifortress}"
db_name="${POSTGRES_DB:-artifortress}"
backup_path="${BACKUP_PATH:-/tmp/artifortress-backup-$(date +%Y%m%d-%H%M%S).sql}"

mkdir -p "$(dirname "$backup_path")"

echo "Creating Postgres backup for database '$db_name' at '$backup_path'..."
docker compose exec -T postgres pg_dump -v -U "$db_user" -d "$db_name" --no-owner --no-privileges > "$backup_path"

if [ ! -s "$backup_path" ]; then
  echo "Backup file was created but is empty: $backup_path"
  exit 1
fi

echo "Backup completed: $backup_path"
