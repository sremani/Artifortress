create table if not exists users (
  user_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  subject text not null,
  display_name text null,
  email citext null,
  created_at timestamptz not null default now(),
  unique (tenant_id, subject)
);

create table if not exists personal_access_tokens (
  token_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  subject text not null,
  token_hash char(64) not null unique,
  scopes text[] not null default '{}'::text[],
  expires_at timestamptz not null,
  revoked_at timestamptz null,
  created_by_subject text not null,
  created_at timestamptz not null default now(),
  check (expires_at > created_at)
);

create table if not exists token_revocations (
  revocation_id bigint generated always as identity primary key,
  tenant_id uuid not null references tenants(tenant_id),
  token_id uuid not null references personal_access_tokens(token_id) on delete cascade,
  revoked_by_subject text not null,
  reason text not null default 'manual_revocation',
  revoked_at timestamptz not null default now()
);

create table if not exists role_bindings (
  binding_id bigint generated always as identity primary key,
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id) on delete cascade,
  subject text not null,
  roles text[] not null,
  updated_by_subject text not null,
  updated_at timestamptz not null default now(),
  unique (tenant_id, repo_id, subject)
);

create index if not exists idx_pat_hash_lookup
  on personal_access_tokens (token_hash);

create index if not exists idx_pat_subject
  on personal_access_tokens (tenant_id, subject);

create index if not exists idx_pat_active
  on personal_access_tokens (tenant_id, expires_at)
  where revoked_at is null;

create index if not exists idx_token_revocations_token_id
  on token_revocations (token_id);

create index if not exists idx_role_bindings_repo_subject
  on role_bindings (repo_id, subject);

create index if not exists idx_role_bindings_subject
  on role_bindings (tenant_id, subject);

create index if not exists idx_audit_action_time
  on audit_log (action, occurred_at desc);
