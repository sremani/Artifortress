create table if not exists upload_sessions (
  upload_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id) on delete cascade,
  expected_digest char(64) not null,
  expected_length bigint not null check (expected_length > 0),
  state text not null check (state in ('initiated', 'parts_uploading', 'pending_commit', 'committed', 'aborted')),
  object_staging_key text null,
  storage_upload_id text null,
  committed_blob_digest char(64) null references blobs(digest),
  created_by_subject text not null,
  expires_at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  committed_at timestamptz null,
  aborted_at timestamptz null,
  aborted_reason text null
);

create index if not exists idx_upload_sessions_repo_state
  on upload_sessions (repo_id, state, created_at desc);

create index if not exists idx_upload_sessions_expires_at
  on upload_sessions (expires_at);

create index if not exists idx_upload_sessions_tenant_repo_digest
  on upload_sessions (tenant_id, repo_id, expected_digest);
