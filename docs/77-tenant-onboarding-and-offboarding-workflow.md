# Tenant Onboarding And Offboarding Workflow

Last updated: 2026-04-28

## Purpose

This document defines the supported tenant lifecycle workflow for Artifortress
enterprise operators. It covers tenant creation posture, role binding, identity
mapping, quota assignment, initial repositories, evidence export, legal-hold
review, offboarding, and deletion/retention behavior.

The current product runs one configured tenant per deployed environment. Tenant
creation is therefore an environment/configuration step, not a self-service
multi-tenant API. Lifecycle actions after deployment are API-backed and should
be performed through the supported admin CLI.

## Audit Model

Every lifecycle milestone should use a shared `ARTIFORTRESS_CORRELATION_ID`.
Operators must record explicit lifecycle markers with:

```bash
make admin-cli ARGS="tenant lifecycle mark --step <step> --status <status> --reason <reason>"
```

Supported steps:

- `tenant.created`
- `roles.bound`
- `identity.mapped`
- `quotas.assigned`
- `repository.created`
- `evidence.exported`
- `legal_holds.reviewed`
- `offboarding.started`
- `access.revoked`
- `retention.reviewed`
- `deletion.blocked`
- `deletion.completed`
- `offboarding.completed`

These markers emit `tenant.lifecycle.<step>` audit events with resource type
`tenant_lifecycle`.

## Onboarding Workflow

1. Assign a lifecycle correlation id:

```bash
export ARTIFORTRESS_URL=https://artifortress.example.com
export ARTIFORTRESS_TOKEN=<admin-token>
export ARTIFORTRESS_CORRELATION_ID=$(uuidgen)
```

2. Confirm the environment tenant slug/name are configured and the API is
   ready:

```bash
make admin-cli ARGS="preflight"
make admin-cli ARGS="tenant lifecycle mark --step tenant.created --status completed --reason environment tenant initialized"
```

3. Bind tenant administrators, operators, and auditors:

```bash
make admin-cli ARGS="tenant roles set --subject platform-admin@example.com --role admin"
make admin-cli ARGS="tenant roles set --subject auditor@example.com --role auditor"
make admin-cli ARGS="tenant lifecycle mark --step roles.bound --status completed --reason initial tenant roles assigned"
```

4. Configure identity-provider role mappings in the deployment environment and
   validate a federated principal:

```bash
make admin-cli ARGS="auth whoami"
make admin-cli ARGS="tenant lifecycle mark --step identity.mapped --status completed --reason federated access validated"
```

5. Assign tenant quotas and governance defaults:

```bash
make admin-cli ARGS="tenant admission set --max-logical-storage-bytes 53687091200 --max-concurrent-upload-sessions 100 --max-pending-search-jobs 1000"
make admin-cli ARGS="tenant governance set --min-tombstone-retention-days 30 --dual-control-tombstone true --dual-control-quarantine true"
make admin-cli ARGS="tenant lifecycle mark --step quotas.assigned --status completed --reason production quota and governance baseline applied"
```

6. Create initial repositories and repository role bindings:

```bash
make admin-cli ARGS="repo create --repo libs-release --type local"
make admin-cli ARGS="repo bindings set --repo libs-release --subject team-builds --role write"
make admin-cli ARGS="tenant lifecycle mark --step repository.created --status completed --repo libs-release --reason initial release repository ready"
```

7. Export baseline evidence:

```bash
make admin-cli ARGS="compliance evidence --audit-limit 500 --approval-limit 250"
make admin-cli ARGS="tenant lifecycle mark --step evidence.exported --status completed --reason onboarding baseline evidence exported"
```

## Offboarding Workflow

1. Start offboarding with an explicit reason:

```bash
export ARTIFORTRESS_CORRELATION_ID=$(uuidgen)
make admin-cli ARGS="tenant lifecycle mark --step offboarding.started --status started --reason contract ended"
```

2. Review legal holds and readiness:

```bash
make admin-cli ARGS="compliance legal-holds"
make admin-cli ARGS="tenant offboarding-readiness"
make admin-cli ARGS="tenant lifecycle mark --step legal_holds.reviewed --status completed --reason active holds reviewed"
```

If `tenant offboarding-readiness` reports active legal holds, do not delete
repositories or artifact bytes. Record a blocked marker:

```bash
make admin-cli ARGS="tenant lifecycle mark --step deletion.blocked --status blocked --reason active legal hold prevents deletion"
```

3. Revoke access:

```bash
make admin-cli ARGS="auth pats revoke-subject --subject departing-admin@example.com --reason tenant offboarding"
make admin-cli ARGS="tenant roles delete --subject departing-admin@example.com"
make admin-cli ARGS="repo bindings delete --repo libs-release --subject departing-admin@example.com"
make admin-cli ARGS="tenant lifecycle mark --step access.revoked --status completed --subject departing-admin@example.com --reason tenant access revoked"
```

Remove or narrow every repository role binding before final retention review. If
the identity provider grants access through groups, remove those group
memberships outside Artifortress and record the external change ticket in the
lifecycle marker reason.

4. Export final evidence:

```bash
make admin-cli ARGS="compliance evidence --audit-limit 1000 --approval-limit 500"
make admin-cli ARGS="tenant lifecycle mark --step evidence.exported --status completed --reason final offboarding evidence exported"
```

5. Review retention:

```bash
make admin-cli ARGS="tenant offboarding-readiness"
make admin-cli ARGS="tenant lifecycle mark --step retention.reviewed --status completed --reason retention owner approved deletion window --retention-until-utc 2026-05-28T00:00:00Z"
```

## Deletion And Retention Behavior

Artifortress does not support blind tenant hard-delete as a routine operation.
Deletion must be retention-aware:

- active legal holds block destructive deletion
- final compliance evidence must be exported before destructive cleanup
- PATs and role bindings must be revoked or narrowed before final shutdown
- repositories should be tombstoned/deleted only after the customer retention
  owner approves the retention disposition
- GC execute mode must only run after legal-hold and retention review pass

When deletion is blocked, preserve the environment and continue access
lockdown. When deletion is approved, record:

```bash
make admin-cli ARGS="tenant lifecycle mark --step deletion.completed --status completed --reason retention-approved cleanup completed"
make admin-cli ARGS="tenant lifecycle mark --step offboarding.completed --status completed --reason tenant offboarding complete"
```

## API Support

The workflow is backed by:

- `POST /v1/admin/tenant-lifecycle/events`
- `GET /v1/admin/tenant-lifecycle/offboarding-readiness`
- existing tenant role, PAT, repository, governance, compliance, legal-hold,
  and audit endpoints

`GET /v1/admin/tenant-lifecycle/offboarding-readiness` reports active legal
holds, active PATs, tenant role bindings, repository role bindings, repository
count, and whether deletion is blocked by legal hold or pending access
revocation.
