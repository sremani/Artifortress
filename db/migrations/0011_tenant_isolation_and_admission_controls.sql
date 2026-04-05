create table if not exists tenant_admission_policies (
  tenant_id uuid primary key references tenants(tenant_id) on delete cascade,
  max_logical_storage_bytes bigint not null check (max_logical_storage_bytes >= 10485760),
  max_concurrent_upload_sessions int not null check (max_concurrent_upload_sessions between 1 and 1000),
  max_pending_search_jobs int not null check (max_pending_search_jobs between 1 and 50000),
  updated_by_subject text not null,
  updated_at timestamptz not null default now()
);

create index if not exists idx_search_index_jobs_tenant_status
  on search_index_jobs (tenant_id, status, updated_at desc);

create index if not exists idx_upload_sessions_tenant_state
  on upload_sessions (tenant_id, state, created_at desc);
