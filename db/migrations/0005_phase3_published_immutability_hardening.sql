create or replace function deny_published_version_mutation()
returns trigger language plpgsql as $$
begin
  if new.state = 'published' and new.published_at is null then
    raise exception 'published_at is required when state is published';
  end if;

  if new.state = 'tombstoned' and new.tombstoned_at is null then
    raise exception 'tombstoned_at is required when state is tombstoned';
  end if;

  if old.state = 'published' then
    if new.version <> old.version
       or new.package_id <> old.package_id
       or new.repo_id <> old.repo_id
       or new.tenant_id <> old.tenant_id
       or new.created_by_subject <> old.created_by_subject
       or new.created_at <> old.created_at
       or new.published_at is distinct from old.published_at then
      raise exception 'published version metadata is immutable';
    end if;

    if new.state not in ('published', 'tombstoned') then
      raise exception 'published version may only transition to tombstoned';
    end if;

    if new.state = 'published' then
      if new.tombstoned_at is distinct from old.tombstoned_at
         or new.tombstone_reason is distinct from old.tombstone_reason then
        raise exception 'tombstone metadata cannot change while version is published';
      end if;
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
