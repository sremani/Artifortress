create table if not exists tombstones (
  tombstone_id bigint generated always as identity primary key,
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id),
  version_id uuid not null unique references package_versions(version_id) on delete cascade,
  deleted_by_subject text not null,
  deleted_at timestamptz not null default now(),
  retention_until timestamptz not null,
  reason text not null
);

create index if not exists idx_tombstones_retention
  on tombstones (tenant_id, retention_until);

create table if not exists gc_runs (
  run_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  initiated_by_subject text not null,
  mode text not null check (mode in ('dry_run', 'execute')),
  retention_grace_hours int not null check (retention_grace_hours >= 0),
  batch_size int not null check (batch_size > 0),
  started_at timestamptz not null default now(),
  completed_at timestamptz null,
  marked_count int not null default 0,
  candidate_blob_count int not null default 0,
  deleted_blob_count int not null default 0,
  deleted_version_count int not null default 0,
  delete_error_count int not null default 0
);

create index if not exists idx_gc_runs_tenant_started
  on gc_runs (tenant_id, started_at desc);

create table if not exists gc_marks (
  run_id uuid not null references gc_runs(run_id) on delete cascade,
  digest char(64) not null references blobs(digest) on delete cascade,
  marked_at timestamptz not null default now(),
  primary key (run_id, digest)
);

create index if not exists idx_gc_marks_digest
  on gc_marks (digest);

alter table if exists upload_sessions
  drop constraint if exists upload_sessions_committed_blob_digest_fkey;

alter table if exists upload_sessions
  add constraint upload_sessions_committed_blob_digest_fkey
  foreign key (committed_blob_digest)
  references blobs(digest)
  on delete set null;
