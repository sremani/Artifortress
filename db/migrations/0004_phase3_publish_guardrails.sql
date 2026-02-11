create index if not exists idx_package_versions_state
  on package_versions (repo_id, state, published_at desc);

create index if not exists idx_outbox_aggregate_pending
  on outbox_events (aggregate_type, aggregate_id, available_at)
  where delivered_at is null;

create or replace function deny_published_version_mutation()
returns trigger language plpgsql as $$
begin
  if old.state = 'published' then
    if new.version <> old.version
       or new.package_id <> old.package_id
       or new.repo_id <> old.repo_id
       or new.tenant_id <> old.tenant_id then
      raise exception 'published version metadata is immutable';
    end if;
  end if;

  return new;
end
$$;

drop trigger if exists trg_deny_published_version_mutation on package_versions;

create trigger trg_deny_published_version_mutation
before update on package_versions
for each row
execute function deny_published_version_mutation();
