# Upgrade and Rollback Runbook

Last updated: 2026-02-11

## Purpose

Define deterministic upgrade and rollback procedures for Artifortress GA operations.

## Preconditions

- deployment window approved
- latest backup snapshot available
- CI green on target commit
- rollback owner and comms channel assigned

## Upgrade Procedure

1. Capture pre-upgrade backup:

```bash
make db-backup
```

2. Record baseline ops snapshot:

```bash
# requires admin token
curl -sS -H "Authorization: Bearer <admin-token>" http://127.0.0.1:8086/v1/admin/ops/summary
```

3. Deploy new API/worker binaries.
4. Apply DB migrations:

```bash
make db-migrate
```

5. Verify health/readiness:

```bash
curl -sS http://127.0.0.1:8086/health/live
curl -sS http://127.0.0.1:8086/health/ready
```

6. Run smoke and integration checks in environment-specific pipeline.
7. Run post-upgrade ops summary and compare against baseline.
8. Declare upgrade successful only when:
- readiness stable
- error budget impact acceptable
- no unexpected backlog growth

## Rollback Triggers

Rollback immediately if any of the following occurs:
- persistent `not_ready` responses from `/health/ready`
- sustained increase in failed search jobs or pending outbox age above alert thresholds
- migration-induced correctness failures or data inconsistency
- unresolved P0 production incident during upgrade window

## Rollback Procedure

1. Stop new API/worker release.
2. Redeploy previous known-good binaries.
3. If schema/data rollback is required, restore from latest backup:

```bash
RESTORE_PATH=/tmp/artifortress-backup.sql make db-restore
```

4. Re-run migrations compatible with rollback baseline (if needed).
5. Validate:
- `/health/live` and `/health/ready`
- core smoke actions (auth, repo read, upload/download where applicable)
- `/v1/admin/ops/summary` backlog indicators

6. Communicate incident status and next action window.

## Post-Rollback Follow-up

- capture root cause and remediation ticket
- update threat/risk logs if control gaps were discovered
- re-run `make phase6-drill` before next upgrade attempt
