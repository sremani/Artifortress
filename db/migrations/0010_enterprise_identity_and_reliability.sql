alter table personal_access_tokens
  add column if not exists last_used_at timestamptz null,
  add column if not exists last_used_auth_source text null;

create table if not exists pat_issuance_policies (
  tenant_id uuid primary key references tenants(tenant_id) on delete cascade,
  max_ttl_minutes int not null check (max_ttl_minutes between 5 and 10080),
  allow_bootstrap_issuance boolean not null default true,
  updated_by_subject text not null,
  updated_at timestamptz not null default now()
);

create index if not exists idx_pat_last_used
  on personal_access_tokens (tenant_id, last_used_at desc nulls last);

create index if not exists idx_pat_active_subject
  on personal_access_tokens (tenant_id, subject, revoked_at, expires_at);
