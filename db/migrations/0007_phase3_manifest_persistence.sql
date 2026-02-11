create table if not exists manifests (
  version_id uuid primary key references package_versions(version_id) on delete cascade,
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id),
  package_type text not null,
  manifest_blob_digest char(64) null references blobs(digest),
  manifest_json jsonb not null,
  created_by_subject text not null,
  updated_by_subject text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index if not exists idx_manifests_repo_type
  on manifests (repo_id, package_type);

create index if not exists idx_manifests_tenant_repo
  on manifests (tenant_id, repo_id);
