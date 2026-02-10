#!/usr/bin/env bash
set -euo pipefail

max_attempts="${1:-60}"
db_user="${POSTGRES_USER:-artifortress}"
db_name="${POSTGRES_DB:-artifortress}"

for ((attempt=1; attempt<=max_attempts; attempt++)); do
  if docker compose exec -T postgres pg_isready -U "$db_user" -d "$db_name" >/dev/null 2>&1; then
    echo "Postgres is ready."
    exit 0
  fi

  echo "Waiting for Postgres ($attempt/$max_attempts)..."
  sleep 2
done

echo "Postgres did not become ready in time."
exit 1
