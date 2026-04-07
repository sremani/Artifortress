# Upgrade Compatibility Matrix

Last updated: 2026-04-06

## Purpose

Define the supported upgrade and rollback paths for currently supported Artifortress baselines.

## Supported Upgrade Paths

| From Baseline | Schema Marker | To | Status | Validation |
|---|---|---|---|---|
| Phase 6 GA baseline | `0009_post_ga_search_read_model.sql` | current head | supported | exercised by `scripts/upgrade-compatibility-drill.sh` |
| Enterprise identity baseline | `0010_enterprise_identity_and_reliability.sql` | current head | supported | exercised by `scripts/upgrade-compatibility-drill.sh` |
| Tenant delegation baseline | `0012_tenant_role_bindings_and_audit_correlation.sql` | current head | supported | exercised by `scripts/upgrade-compatibility-drill.sh` |
| Current head | latest migration in `db/migrations` | current head | supported | exercised continuously by CI and `make db-migrate` |

## Rollback Matrix

| Deployed Shape | Application Rollback Only | Database Restore Required | Notes |
|---|---|---|---|
| Class A migration only | yes | no | schema remains backward compatible |
| Class B migration with active backfill/rebuild | usually yes | no | tolerate bounded async/search degradation during cutback |
| Class C contract-tightening migration | no | yes | restore is the supported rollback path |
| Current enterprise governance wave (`0010` through `0013`) | yes for app-only rollback while schema remains at head | no for app-only, yes if schema rollback is required | current migrations are expand-oriented and forward-safe |

## Test Procedure

Use:

```bash
./scripts/upgrade-compatibility-drill.sh
```

The drill:

1. creates a temporary Postgres database per supported baseline
2. applies migrations only through the baseline marker
3. upgrades that database to head with the normal migration runner
4. verifies smoke schema invariants on the upgraded database

## Support Boundary

This repository currently supports:

- forward upgrade from the listed baselines to head
- app-only rollback while schema compatibility remains intact
- restore-based rollback for any path that breaks compatibility

This repository does not currently claim:

- arbitrary rollback to historical binary revisions without restore
- reverse-SQL downgrade support for destructive migrations
- active support for baselines older than the listed markers

## References

- `docs/25-upgrade-rollback-runbook.md`
- `docs/41-migration-compatibility-policy.md`
- `scripts/upgrade-compatibility-drill.sh`
