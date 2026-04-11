alter table search_index_jobs
  drop constraint if exists search_index_jobs_status_check;

alter table search_index_jobs
  add constraint search_index_jobs_status_check
  check (status in ('pending', 'processing', 'completed', 'failed', 'cancelled'));

create table if not exists tenant_search_controls (
  tenant_id uuid primary key references tenants(tenant_id) on delete cascade,
  is_paused boolean not null default false,
  pause_reason text null,
  updated_by_subject text not null,
  updated_at timestamptz not null default now()
);

create index if not exists idx_search_index_jobs_tenant_status_updated
  on search_index_jobs (tenant_id, status, updated_at desc);
