#!/usr/bin/env bash
set -euo pipefail

project_name="${COMPOSE_PROJECT_NAME:-kublai}"
network_name="${project_name}_default"
bucket_name="${MINIO_BUCKET:-kublai-dev}"
access_key="${MINIO_ROOT_USER:-kublai}"
secret_key="${MINIO_ROOT_PASSWORD:-kublai-secret}"

docker run --rm --entrypoint /bin/sh --network "$network_name" minio/mc:latest -c "
  mc alias set local http://minio:9000 $access_key $secret_key >/dev/null &&
  mc mb --ignore-existing local/$bucket_name >/dev/null
"

echo "MinIO bucket ensured: $bucket_name"
