create table if not exists search_documents (
  document_id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants(tenant_id),
  repo_id uuid not null references repos(repo_id) on delete cascade,
  version_id uuid not null references package_versions(version_id) on delete cascade,
  repo_key text not null,
  package_type text not null,
  package_namespace text null,
  package_name text not null,
  package_version text not null,
  manifest_json jsonb null,
  published_at timestamptz null,
  search_text text not null,
  indexed_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  search_vector tsvector generated always as (to_tsvector('simple', search_text)) stored,
  unique (tenant_id, version_id)
);

create index if not exists idx_search_documents_repo_name
  on search_documents (tenant_id, repo_key, package_name, package_version desc);

create index if not exists idx_search_documents_vector
  on search_documents using gin (search_vector);
