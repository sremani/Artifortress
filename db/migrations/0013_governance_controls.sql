create table if not exists tenant_governance_policies (
  tenant_id uuid primary key references tenants(tenant_id) on delete cascade,
  min_tombstone_retention_days int not null check (min_tombstone_retention_days between 1 and 3650),
  require_dual_control_for_tombstone boolean not null default false,
  require_dual_control_for_quarantine_resolution boolean not null default false,
  updated_by_subject text not null,
  updated_at timestamptz not null default now()
);

create table if not exists repo_governance_policies (
  repo_policy_id bigint generated always as identity primary key,
  tenant_id uuid not null references tenants(tenant_id) on delete cascade,
  repo_id uuid not null references repos(repo_id) on delete cascade,
  min_tombstone_retention_days int null check (min_tombstone_retention_days between 1 and 3650),
  require_dual_control_for_tombstone boolean null,
  require_dual_control_for_quarantine_resolution boolean null,
  updated_by_subject text not null,
  updated_at timestamptz not null default now(),
  unique (tenant_id, repo_id)
);

create table if not exists artifact_protections (
  protection_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id) on delete cascade,
  repo_id uuid not null references repos(repo_id) on delete cascade,
  version_id uuid not null references package_versions(version_id) on delete cascade,
  mode text not null check (mode in ('protected', 'legal_hold')),
  reason text not null,
  protected_by_subject text not null,
  protected_at timestamptz not null default now(),
  released_at timestamptz null,
  released_by_subject text null,
  release_reason text null,
  unique (tenant_id, version_id)
);

create index if not exists idx_artifact_protections_active
  on artifact_protections (tenant_id, repo_id, released_at, mode);

create table if not exists governance_approvals (
  approval_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id) on delete cascade,
  repo_id uuid not null references repos(repo_id) on delete cascade,
  action text not null,
  resource_type text not null,
  resource_id text not null,
  justification text not null,
  requested_by_subject text not null,
  requested_at timestamptz not null default now(),
  approved_by_subject text null,
  approved_at timestamptz null,
  status text not null check (status in ('pending', 'approved', 'consumed', 'cancelled'))
);

create index if not exists idx_governance_approvals_repo_status
  on governance_approvals (tenant_id, repo_id, status, requested_at desc);
