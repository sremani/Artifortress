create table if not exists tenant_role_bindings (
  binding_id bigint generated always as identity primary key,
  tenant_id uuid not null references tenants(tenant_id) on delete cascade,
  subject text not null,
  roles text[] not null,
  updated_by_subject text not null,
  updated_at timestamptz not null default now(),
  unique (tenant_id, subject)
);

create index if not exists idx_tenant_role_bindings_subject
  on tenant_role_bindings (tenant_id, subject);

alter table audit_log
  add column if not exists correlation_id uuid not null default gen_random_uuid();

create index if not exists idx_audit_log_tenant_occurred
  on audit_log (tenant_id, occurred_at desc);

create index if not exists idx_audit_log_correlation
  on audit_log (tenant_id, correlation_id, occurred_at desc);
