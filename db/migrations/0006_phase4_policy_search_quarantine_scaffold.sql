create table if not exists policy_evaluations (
  evaluation_id bigint generated always as identity primary key,
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id) on delete cascade,
  version_id uuid null references package_versions(version_id) on delete set null,
  action text not null check (action in ('publish', 'promote')),
  decision text not null check (decision in ('allow', 'deny', 'quarantine')),
  policy_engine_version text null,
  reason text not null,
  details jsonb not null default '{}'::jsonb,
  evaluated_at timestamptz not null default now(),
  evaluated_by_subject text null
);

create index if not exists idx_policy_evaluations_repo_time
  on policy_evaluations (repo_id, evaluated_at desc);

create index if not exists idx_policy_evaluations_decision_time
  on policy_evaluations (decision, evaluated_at desc);

create table if not exists quarantine_items (
  quarantine_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id) on delete cascade,
  version_id uuid not null references package_versions(version_id) on delete cascade,
  status text not null check (status in ('quarantined', 'released', 'rejected')),
  reason text not null,
  created_at timestamptz not null default now(),
  resolved_at timestamptz null,
  resolved_by_subject text null,
  unique (tenant_id, repo_id, version_id)
);

create index if not exists idx_quarantine_items_status_created
  on quarantine_items (status, created_at desc);

create index if not exists idx_quarantine_items_repo_status
  on quarantine_items (repo_id, status, created_at desc);

create table if not exists search_index_jobs (
  job_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  version_id uuid not null references package_versions(version_id) on delete cascade,
  status text not null check (status in ('pending', 'processing', 'completed', 'failed')),
  available_at timestamptz not null default now(),
  attempts int not null default 0 check (attempts >= 0),
  last_error text null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, version_id)
);

create index if not exists idx_search_index_jobs_pending
  on search_index_jobs (status, available_at)
  where status in ('pending', 'failed');
