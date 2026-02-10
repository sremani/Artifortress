create extension if not exists pgcrypto;
create extension if not exists citext;

create table if not exists tenants (
  tenant_id uuid primary key default gen_random_uuid(),
  slug citext not null unique,
  name text not null,
  created_at timestamptz not null default now()
);

create table if not exists repos (
  repo_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_key citext not null,
  repo_type text not null check (repo_type in ('local', 'remote', 'virtual')),
  config jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  unique (tenant_id, repo_key)
);

create table if not exists packages (
  package_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id),
  package_type text not null,
  namespace text null,
  name text not null,
  created_at timestamptz not null default now()
);

create unique index if not exists ux_packages_coordinate
  on packages (repo_id, package_type, coalesce(namespace, ''), name);

create table if not exists blobs (
  digest char(64) primary key,
  length_bytes bigint not null check (length_bytes > 0),
  storage_key text not null unique,
  object_etag text null,
  created_at timestamptz not null default now()
);

create table if not exists package_versions (
  version_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id),
  package_id uuid not null references packages(package_id),
  version text not null,
  state text not null check (state in ('draft', 'published', 'tombstoned')),
  published_at timestamptz null,
  created_by_subject text not null,
  created_at timestamptz not null default now(),
  tombstoned_at timestamptz null,
  tombstone_reason text null,
  unique (repo_id, package_id, version)
);

create table if not exists artifact_entries (
  entry_id bigint generated always as identity primary key,
  version_id uuid not null references package_versions(version_id) on delete cascade,
  relative_path text not null,
  blob_digest char(64) not null references blobs(digest),
  checksum_sha1 char(40) null,
  checksum_sha256 char(64) null,
  size_bytes bigint not null check (size_bytes > 0),
  unique (version_id, relative_path)
);

create table if not exists audit_log (
  audit_id bigint generated always as identity primary key,
  tenant_id uuid not null,
  actor_subject text not null,
  action text not null,
  resource_type text not null,
  resource_id text not null,
  details jsonb not null default '{}'::jsonb,
  occurred_at timestamptz not null default now()
);

create table if not exists outbox_events (
  event_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null,
  aggregate_type text not null,
  aggregate_id text not null,
  event_type text not null,
  payload jsonb not null,
  occurred_at timestamptz not null default now(),
  available_at timestamptz not null default now(),
  delivered_at timestamptz null,
  delivery_attempts int not null default 0
);

create index if not exists idx_packages_repo_lookup
  on packages (repo_id, package_type, namespace, name);

create index if not exists idx_package_versions_resolve
  on package_versions (repo_id, package_id, version);

create index if not exists idx_artifact_entries_version
  on artifact_entries (version_id);

create index if not exists idx_artifact_entries_blob
  on artifact_entries (blob_digest);

create index if not exists idx_outbox_pending
  on outbox_events (available_at)
  where delivered_at is null;
