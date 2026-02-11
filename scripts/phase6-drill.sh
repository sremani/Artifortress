#!/usr/bin/env bash
set -euo pipefail

source_db="${POSTGRES_DB:-artifortress}"
db_user="${POSTGRES_USER:-artifortress}"
drill_db="${DRILL_DB:-artifortress_drill}"
backup_path="${BACKUP_PATH:-/tmp/artifortress-phase6-drill-$(date +%Y%m%d-%H%M%S).sql}"
report_path="${REPORT_PATH:-docs/reports/phase6-rto-rpo-drill-latest.md}"

required_tables=(
  schema_migrations
  tenants
  repos
  packages
  blobs
  package_versions
  artifact_entries
  outbox_events
  search_index_jobs
  gc_runs
)

psql_scalar() {
  local db_name="$1"
  local query="$2"
  docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$db_user" -d "$db_name" -tAc "$query" | tr -d '\n' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

start_epoch="$(date +%s)"

echo "[phase6-drill] Starting backup step..."
backup_start="$(date +%s)"
BACKUP_PATH="$backup_path" POSTGRES_DB="$source_db" POSTGRES_USER="$db_user" ./scripts/db-backup.sh
backup_end="$(date +%s)"
backup_duration="$((backup_end - backup_start))"

echo "[phase6-drill] Starting restore step into '$drill_db'..."
restore_start="$(date +%s)"
RESTORE_PATH="$backup_path" TARGET_DB="$drill_db" POSTGRES_USER="$db_user" ./scripts/db-restore.sh
restore_end="$(date +%s)"
restore_duration="$((restore_end - restore_start))"

verification_result="PASS"
verification_notes=()

for table_name in "${required_tables[@]}"; do
  source_count="$(psql_scalar "$source_db" "select count(*) from ${table_name};")"
  drill_count="$(psql_scalar "$drill_db" "select count(*) from ${table_name};")"

  if [ "$source_count" != "$drill_count" ]; then
    verification_result="FAIL"
    verification_notes+=("count mismatch on ${table_name}: source=${source_count}, drill=${drill_count}")
  fi
done

total_duration="$(( $(date +%s) - start_epoch ))"
rto_target_seconds="${RTO_TARGET_SECONDS:-900}"
rpo_target_seconds="${RPO_TARGET_SECONDS:-300}"

rto_status="PASS"
if [ "$total_duration" -gt "$rto_target_seconds" ]; then
  rto_status="FAIL"
  verification_result="FAIL"
  verification_notes+=("RTO exceeded target (${total_duration}s > ${rto_target_seconds}s)")
fi

rpo_status="PASS"
if [ "$backup_duration" -gt "$rpo_target_seconds" ]; then
  rpo_status="FAIL"
  verification_result="FAIL"
  verification_notes+=("RPO backup window exceeded target (${backup_duration}s > ${rpo_target_seconds}s)")
fi

mkdir -p "$(dirname "$report_path")"

{
  echo "# Phase 6 RPO/RTO Drill Report"
  echo
  echo "Generated at: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "## Inputs"
  echo
  echo "- source DB: \`${source_db}\`"
  echo "- drill DB: \`${drill_db}\`"
  echo "- backup file: \`${backup_path}\`"
  echo "- RTO target (seconds): ${rto_target_seconds}"
  echo "- RPO target (seconds): ${rpo_target_seconds}"
  echo
  echo "## Results"
  echo
  echo "- backup duration (seconds): ${backup_duration}"
  echo "- restore duration (seconds): ${restore_duration}"
  echo "- total drill duration (seconds): ${total_duration}"
  echo "- RPO status: ${rpo_status}"
  echo "- RTO status: ${rto_status}"
  echo "- data verification: ${verification_result}"
  echo
  echo "## Verification Notes"
  echo

  if [ "${#verification_notes[@]}" -eq 0 ]; then
    echo "- all required table counts matched between source and drill databases."
  else
    for note in "${verification_notes[@]}"; do
      echo "- ${note}"
    done
  fi
} > "$report_path"

echo "[phase6-drill] Report written to $report_path"

if [ "$verification_result" != "PASS" ]; then
  echo "[phase6-drill] Drill failed."
  exit 1
fi

echo "[phase6-drill] Drill passed."
