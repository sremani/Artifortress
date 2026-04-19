create index if not exists idx_outbox_version_published_claim
  on outbox_events (occurred_at, available_at, tenant_id)
  where delivered_at is null
    and event_type = 'version.published';

drop index if exists idx_search_index_jobs_pending;

create index if not exists idx_search_index_jobs_pending_claim
  on search_index_jobs (status, available_at, created_at, tenant_id)
  where status in ('pending', 'failed');

create index if not exists idx_search_index_jobs_processing_reclaim
  on search_index_jobs (status, updated_at, tenant_id)
  where status = 'processing';
