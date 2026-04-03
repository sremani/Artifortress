# Migration Compatibility Policy

Last updated: 2026-04-01

## Purpose

Define bounded-downtime expectations for schema changes and make rollback assumptions explicit before production upgrades.

## Migration Classes

### Class A: Expand-Only

Examples:
- add nullable columns
- add new tables
- add backward-compatible indexes

Expectation:
- zero application downtime target
- safe for rolling API/worker deployment after migration

### Class B: Expand and Backfill

Examples:
- add new column plus async/read-path adoption
- derived data rebuilds

Expectation:
- bounded operational degradation is acceptable
- no control-plane outage required
- rollout must preserve compatibility between previous and next binaries during backfill window

### Class C: Contract / Tightening

Examples:
- drop columns
- change constraints that invalidate old binaries
- destructive table rewrites

Expectation:
- explicitly scheduled maintenance window
- bounded downtime expected unless proven otherwise
- rollback requires restore or proven reverse migration path

## Repository Policy

Current repository standard:

1. Default new migrations to `Class A` whenever possible.
2. If a change cannot be `Class A`, split it into:
   - expand
   - code rollout
   - data/backfill
   - optional contract cleanup
3. No `Class C` migration may ship without:
   - rollback owner
   - restore evidence
   - explicit maintenance-window note in release plan

## Rollout Sequence

### For Class A

1. backup
2. deploy migration
3. deploy new API/worker
4. verify readiness and ops summary

### For Class B

1. backup
2. deploy expand migration
3. deploy new API/worker in compatibility mode
4. run or monitor backfill/rebuild
5. verify backlog and readiness remain inside thresholds
6. complete cutover only after backfill reaches acceptable state

### For Class C

1. backup and restore checkpoint
2. approved maintenance window
3. remove traffic or accept bounded outage
4. apply migration
5. deploy compatible binaries
6. verify smoke checks
7. if verification fails, restore prior binaries and database from backup

## Downtime Statement

- `Class A`: no planned downtime
- `Class B`: no planned full downtime, but degraded async/search behavior may occur during rebuild windows
- `Class C`: bounded downtime required unless separately proven safe

## Rollback Policy

- application-only rollback is valid only when schema remains backward compatible
- if backward compatibility is broken, rollback requires restore, not guesswork
- do not attempt ad hoc destructive reverse SQL in incident response unless pre-rehearsed

## Pre-Release Checklist for Migrations

1. classify migration as `A`, `B`, or `C`
2. document compatibility assumptions in release notes
3. verify `make db-migrate` on clean environment
4. verify upgrade runbook and restore path
5. verify readiness and ops-summary behavior after rollout

## Required References

- `docs/25-upgrade-rollback-runbook.md`
- `docs/40-ha-topology-and-failure-domains.md`
- `docs/42-operations-dashboard-and-drill-bundle.md`
