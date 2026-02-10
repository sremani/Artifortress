#!/usr/bin/env bash
set -euo pipefail

project_name="${COMPOSE_PROJECT_NAME:-artifortress}"
network_name="${project_name}_default"
bucket_name="${MINIO_BUCKET:-artifortress-dev}"
access_key="${MINIO_ROOT_USER:-artifortress}"
secret_key="${MINIO_ROOT_PASSWORD:-artifortress}"

docker run --rm --network "$network_name" minio/mc:latest /bin/sh -c "
  mc alias set local http://minio:9000 $access_key $secret_key >/dev/null &&
  mc mb --ignore-existing local/$bucket_name >/dev/null
"

echo "MinIO bucket ensured: $bucket_name"
