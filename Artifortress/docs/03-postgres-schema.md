# Postgres Schema Sketch and Critical Indexes

This schema emphasizes correctness boundaries:
- immutable content identity,
- atomic publish transactions,
- append-only audit and outbox side effects.

## 1. Extensions

```sql
create extension if not exists pgcrypto;
create extension if not exists citext;
```

## 2. Core Tables (DDL Sketch)

```sql
create table tenants (
  tenant_id uuid primary key default gen_random_uuid(),
  slug citext not null unique,
  name text not null,
  created_at timestamptz not null default now()
);

create table repos (
  repo_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_key citext not null,
  repo_type text not null check (repo_type in ('local','remote','virtual')),
  config jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  unique (tenant_id, repo_key)
);

create table packages (
  package_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id),
  package_type text not null,
  namespace text null,
  name text not null,
  created_at timestamptz not null default now()
);

create table blobs (
  digest char(64) primary key,
  length_bytes bigint not null check (length_bytes > 0),
  storage_key text not null unique,
  object_etag text null,
  created_at timestamptz not null default now()
);

create table upload_sessions (
  upload_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id),
  expected_digest char(64) not null,
  expected_length bigint not null check (expected_length > 0),
  state text not null check (state in ('initiated','parts_uploading','pending_commit','committed','aborted')),
  object_staging_key text null,
  expires_at timestamptz not null,
  created_at timestamptz not null default now()
);

create table package_versions (
  version_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id),
  package_id uuid not null references packages(package_id),
  version text not null,
  state text not null check (state in ('draft','published','tombstoned')),
  published_at timestamptz null,
  created_by_subject text not null,
  created_at timestamptz not null default now(),
  tombstoned_at timestamptz null,
  tombstone_reason text null,
  unique (repo_id, package_id, version)
);

create table manifests (
  version_id uuid primary key references package_versions(version_id) on delete cascade,
  manifest_blob_digest char(64) null references blobs(digest),
  normalized jsonb not null,
  schema_version int not null default 1
);

create table artifact_entries (
  entry_id bigint generated always as identity primary key,
  version_id uuid not null references package_versions(version_id) on delete cascade,
  relative_path text not null,
  blob_digest char(64) not null references blobs(digest),
  checksum_sha1 char(40) null,
  checksum_sha256 char(64) null,
  size_bytes bigint not null check (size_bytes > 0),
  unique (version_id, relative_path)
);

create table attestations (
  attestation_id bigint generated always as identity primary key,
  version_id uuid not null references package_versions(version_id) on delete cascade,
  predicate_type text not null,
  statement_blob_digest char(64) not null references blobs(digest),
  signer text null,
  created_at timestamptz not null default now()
);

create table audit_log (
  audit_id bigint generated always as identity primary key,
  tenant_id uuid not null,
  actor_subject text not null,
  action text not null,
  resource_type text not null,
  resource_id text not null,
  details jsonb not null default '{}'::jsonb,
  occurred_at timestamptz not null default now()
);

create table outbox_events (
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

create table tombstones (
  tombstone_id bigint generated always as identity primary key,
  tenant_id uuid not null,
  repo_id uuid not null references repos(repo_id),
  version_id uuid not null references package_versions(version_id),
  deleted_by_subject text not null,
  deleted_at timestamptz not null default now(),
  retention_until timestamptz not null,
  reason text not null
);
```

## 3. Immutability Enforcement

Use triggers to prevent mutation of immutable rows once in terminal state.

```sql
create or replace function deny_published_version_mutation()
returns trigger language plpgsql as $$
begin
  if old.state = 'published' then
    if new.version <> old.version
       or new.package_id <> old.package_id
       or new.repo_id <> old.repo_id
       or new.tenant_id <> old.tenant_id then
      raise exception 'published version metadata is immutable';
    end if;
  end if;
  return new;
end
$$;

create trigger trg_deny_published_version_mutation
before update on package_versions
for each row execute function deny_published_version_mutation();
```

Pragmatic rule:
- only `state` transition to `tombstoned` and tombstone metadata fields may change after publish.

## 4. Critical Indexes

```sql
create index idx_packages_repo_lookup
  on packages (repo_id, package_type, namespace, name);

create unique index ux_packages_coordinate
  on packages (repo_id, package_type, coalesce(namespace, ''), name);

create index idx_package_versions_resolve
  on package_versions (repo_id, package_id, version);

create index idx_package_versions_state
  on package_versions (repo_id, state, published_at desc);

create index idx_artifact_entries_version
  on artifact_entries (version_id);

create index idx_artifact_entries_blob
  on artifact_entries (blob_digest);

create index idx_blobs_created_at
  on blobs (created_at);

create index idx_outbox_pending
  on outbox_events (available_at)
  where delivered_at is null;

create index idx_tombstones_retention
  on tombstones (retention_until);

create index idx_audit_tenant_time
  on audit_log (tenant_id, occurred_at desc);
```

## 5. Transaction Boundaries

### Upload Commit

Single transaction:
- ensure `blobs` row exists for digest/size/storage key
- mark upload session committed
- optionally add pre-publish artifact linkage data
- insert `outbox_events` (`upload.committed`)

### Version Publish

Single transaction:
- lock target package/version key
- assert all referenced blob digests exist
- insert/update `package_versions` to `published`
- insert `artifact_entries`, `manifests`, `attestations` (optional)
- insert `audit_log`
- insert `outbox_events` (`version.published`)

## 6. Garbage Collection Strategy

Do not rely on mutable refcount for correctness.

Recommended:
- Mark phase:
  - traverse reachable digests from non-tombstoned versions and tombstoned versions still in retention.
- Sweep phase:
  - delete unmarked blobs older than safety window.
- Verify phase:
  - sample reads and checksum verification before hard delete commit batch.

Optional helper table:

```sql
create table gc_marks (
  run_id uuid not null,
  digest char(64) not null,
  marked_at timestamptz not null default now(),
  primary key (run_id, digest)
);
```

## 7. Partitioning Guidance

Early stage:
- avoid premature partitioning.

Scale stage:
- range-partition `audit_log` and `outbox_events` by month on `occurred_at`.
- retain partitions based on compliance policy.
