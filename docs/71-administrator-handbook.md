# Administrator Handbook

Last updated: 2026-04-28

## Purpose

This handbook is the supported day-0, day-1, and day-2 administrator guide for
an Artifortress enterprise environment.

It is intentionally operational. Use it with:

- `docs/59-enterprise-product-envelope.md`
- `docs/29-deployment-howto-production.md`
- `docs/30-deployment-config-reference.md`
- `docs/31-operations-howto.md`
- `docs/66-diagnostic-error-catalog.md`
- `docs/68-slo-sli-alerting.md`
- `docs/70-production-preflight.md`

When this handbook and the product envelope disagree, the product envelope wins.

## Administrator Responsibilities

Administrators are responsible for:

- maintaining runtime configuration and secrets outside git
- managing initial bootstrap access and PAT policy
- connecting OIDC or SAML identity providers
- creating repositories and assigning repo or tenant roles
- maintaining governance, retention, and legal-hold controls
- monitoring readiness, backlog, search, GC, and dependency health
- running backup, restore, upgrade, rollback, and reliability drills
- collecting redacted diagnostics for support

Normal administration must use supported APIs, scripts, Helm values, or release
artifacts. Direct database edits are not part of the supported routine workflow.

## Access Model

Artifortress supports these administrator access paths:

- bootstrap token for initial PAT issuance
- PATs for service and emergency administrative access
- OIDC bearer tokens with configured issuer, audience, signing material, and
  claim-role mappings
- SAML ACS exchange with configured issuer, service-provider entity id, signing
  trust material, and claim-role mappings

Supported role scopes:

- tenant roles for tenant-wide administration, audit, and governance workflows
- repository roles for repository administration, writes, reads, and policy
  actions
- wildcard repository scopes only where platform administration is intended

Operational rules:

- keep bootstrap-token use narrow and audited
- prefer federated identity for routine human administration
- issue PATs with bounded TTLs and explicit scopes
- rotate bootstrap, OIDC, SAML, object-storage, and database secrets through the
  environment's secret-management system
- review privileged audit events after access-policy changes

## Day-0 Environment Setup

Before installing an enterprise environment, confirm the target deployment is
inside the supported envelope:

1. Kubernetes or equivalent production runtime is available.
2. PostgreSQL is durable, backed up, and reachable from API and worker pods.
3. S3-compatible object storage exists with a dedicated bucket per environment.
4. API and worker secrets are available from the runtime secret manager.
5. Ingress, TLS, DNS, and load-balancer ownership are assigned.
6. Dashboard and alert destinations are ready.
7. Release, rollback, security, operations, and support owners are named.

Use `docs/30-deployment-config-reference.md` for exact runtime keys. Required
production classes include:

- `ConnectionStrings__Postgres`
- `Auth__BootstrapToken`
- OIDC or SAML settings when federation is enabled
- `ObjectStorage__Endpoint`
- `ObjectStorage__AccessKey`
- `ObjectStorage__SecretKey`
- `ObjectStorage__Bucket`
- worker polling, batch, and lease settings where defaults are not acceptable

Do not commit populated environment files.

## Day-1 Bootstrap And Identity

After the API and worker are deployed but before production traffic:

1. Run migrations through the supported migration workflow.
2. Verify liveness and readiness:

```bash
curl -sS https://artifortress.example.com/health/live
curl -sS https://artifortress.example.com/health/ready
```

3. Issue the initial bounded administrative PAT using the bootstrap path:

```bash
curl -sS \
  -H "X-Bootstrap-Token: <bootstrap-token>" \
  -H "Content-Type: application/json" \
  -X POST https://artifortress.example.com/v1/auth/pats \
  -d '{"subject":"platform-admin","scopes":["repo:*:admin"],"ttlMinutes":60}'
```

4. Validate the token:

```bash
curl -sS \
  -H "Authorization: Bearer <admin-token>" \
  https://artifortress.example.com/v1/auth/whoami
```

5. Configure OIDC or SAML for routine human administration.
6. Validate claim-role mappings with a non-bootstrap federated principal.
7. Reduce bootstrap-token exposure after federated administration works.

For identity rollout and fallback details, use `docs/37-phase7-runbook.md` and
`docs/38-phase7-rollout-gates.md`.

## Tenant Administration

Tenant-wide administration uses tenant role bindings and admission policy.

Common endpoints:

- `GET /v1/admin/tenant-role-bindings`
- `PUT /v1/admin/tenant-role-bindings/{subject}`
- `GET /v1/admin/tenant-admission-policy`
- `PUT /v1/admin/tenant-admission-policy`
- `GET /v1/admin/governance/policy`
- `PUT /v1/admin/governance/policy`

Example tenant auditor binding:

```bash
curl -sS \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  -X PUT https://artifortress.example.com/v1/admin/tenant-role-bindings/auditor@example.com \
  -d '{"roles":["tenant_auditor"]}'
```

Change rules:

- capture the requester, approver, and reason before tenant-wide changes
- verify the result by reading bindings back from the API
- check `GET /v1/audit` for the corresponding privileged action
- avoid raw database intervention for tenant role or policy repair

## Repository Administration

Repository administrators create local, remote, or virtual repositories and bind
subjects to repository roles.

Common endpoints:

- `GET /v1/repos`
- `GET /v1/repos/{repoKey}`
- `POST /v1/repos`
- `DELETE /v1/repos/{repoKey}`
- `GET /v1/repos/{repoKey}/bindings`
- `PUT /v1/repos/{repoKey}/bindings/{subject}`
- `GET /v1/repos/{repoKey}/governance/policy`
- `PUT /v1/repos/{repoKey}/governance/policy`

Example local repository:

```bash
curl -sS \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  -X POST https://artifortress.example.com/v1/repos \
  -d '{"repoKey":"libs-release","repoType":"local","upstreamUrl":"","memberRepos":[]}'
```

Example repository binding:

```bash
curl -sS \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  -X PUT https://artifortress.example.com/v1/repos/libs-release/bindings/team-builds \
  -d '{"roles":["write"]}'
```

Before deleting a repository or narrowing roles:

- confirm no active production publisher depends on the repository
- export or review relevant audit records
- check legal holds and governance approvals
- record rollback expectations in the change ticket

## PAT Governance

PATs are administrative and service credentials. Treat them as secrets.

Common endpoints:

- `POST /v1/auth/pats`
- `POST /v1/auth/pats/revoke`
- `GET /v1/admin/auth/pat-policy`
- `PUT /v1/admin/auth/pat-policy`
- `GET /v1/admin/auth/pats`
- `POST /v1/admin/auth/pats/revoke-subject`
- `POST /v1/admin/auth/pats/revoke-all`

Routine practice:

- set a maximum TTL aligned with the organization's credential policy
- keep bootstrap issuance enabled only when operationally required
- revoke subject credentials during offboarding
- revoke all PATs only as an incident action with rollback owner approval
- review `auth.pat.*` audit events after policy or revocation actions

## Audit And Compliance Evidence

Audit and evidence workflows are tenant-scoped and API-driven.

Common endpoints:

- `GET /v1/audit`
- `GET /v1/audit/export`
- `GET /v1/compliance/evidence`
- `GET /v1/compliance/legal-holds`

Evidence export:

```bash
curl -sS \
  -H "Authorization: Bearer <auditor-token>" \
  "https://artifortress.example.com/v1/compliance/evidence?auditLimit=500&approvalLimit=250" \
  -D /tmp/compliance-headers.txt \
  -o /tmp/compliance-evidence-pack.json
```

Verify the saved payload against `X-Artifortress-Compliance-Pack-SHA256` from
the response headers.

For control mapping and procurement review, use:

- `docs/63-procurement-evidence-pack.md`
- `docs/64-soc2-control-mapping.md`

## Governance, Approvals, And Legal Holds

Governance policy can require dual control for high-risk lifecycle actions.

Common endpoints:

- `GET /v1/admin/governance/policy`
- `PUT /v1/admin/governance/policy`
- `GET /v1/repos/{repoKey}/governance/policy`
- `PUT /v1/repos/{repoKey}/governance/policy`
- `POST /v1/repos/{repoKey}/approvals`
- `GET /v1/repos/{repoKey}/approvals`
- `POST /v1/repos/{repoKey}/approvals/{approvalId}/approve`
- `POST /v1/repos/{repoKey}/packages/versions/{versionId}/protection`
- `POST /v1/repos/{repoKey}/packages/versions/{versionId}/protection/release`
- `GET /v1/compliance/legal-holds`

Legal-hold rules:

- active legal holds block destructive lifecycle actions
- GC must not delete blobs rooted by held versions
- release a legal hold only with named owner approval and reason
- preserve audit evidence before and after hold changes

Use `docs/49-compliance-evidence-and-legal-holds.md` for detailed behavior.

## Lifecycle, GC, And Reconciliation

Lifecycle administration covers tombstones, garbage collection, and blob
metadata reconciliation.

Common endpoints:

- `POST /v1/repos/{repoKey}/packages/versions/{versionId}/tombstone`
- `POST /v1/admin/gc/runs`
- `GET /v1/admin/reconcile/blobs`

GC workflow:

1. Confirm readiness is `ready`.
2. Confirm there is no active incident involving storage, legal hold, or audit.
3. Run GC in dry-run mode first.
4. Review candidate counts and legal-hold impact.
5. Run execute mode only after approval.
6. Record the returned run id and counts in the change ticket.
7. Re-check `/v1/admin/ops/summary`.

If reconcile reports drift, freeze risky mutation paths, capture a support
bundle, and use `docs/66-diagnostic-error-catalog.md` for triage.

## Search Operations

Search indexing is asynchronous. Administrators should monitor freshness and
failure signals before and after high-volume publish or rebuild activity.

Common endpoints:

- `GET /v1/repos/{repoKey}/search/packages`
- `POST /v1/admin/search/rebuild`
- `GET /v1/admin/search/status`
- `POST /v1/admin/search/pause`
- `POST /v1/admin/search/resume`
- `POST /v1/admin/search/cancel`

Operational rules:

- check pending and failed search jobs before requesting a rebuild
- keep rebuild batch size conservative during production traffic
- pause or cancel rebuilds when readiness, DB latency, or backlog health is
  under pressure
- verify search recovery through `/v1/admin/search/status` and
  `/v1/admin/ops/summary`

Search assurance details live in `docs/50-search-assurance.md` and
`docs/51-search-backfill-soak.md`.

## Backup, Restore, Upgrade, And Rollback

Administrators must keep recovery evidence current before production cutover and
before release candidates.

Routine commands:

```bash
make db-backup
make phase6-drill
make upgrade-compatibility-drill
make verify-enterprise
```

Production upgrade sequence:

1. approve the deployment window
2. name release, rollback, and communications owners
3. capture backup and ops summary
4. deploy API and worker artifacts
5. apply migrations once
6. verify readiness and smoke flows
7. monitor backlog and error signals during the post-deploy window

Rollback triggers include persistent `not_ready`, migration failure, correctness
regression, destructive lifecycle risk, or sustained backlog growth above alert
thresholds.

Use `docs/25-upgrade-rollback-runbook.md`,
`docs/41-migration-compatibility-policy.md`, and
`docs/48-upgrade-compatibility-matrix.md` before declaring an upgrade safe.

## Daily Operations

Daily checks:

```bash
curl -sS https://artifortress.example.com/health/ready
curl -sS \
  -H "Authorization: Bearer <admin-token>" \
  https://artifortress.example.com/v1/admin/ops/summary
```

Review:

- top-level readiness state
- dependency-level readiness details
- OIDC and SAML trust-material status
- pending outbox count and oldest pending age
- pending and failed search jobs
- incomplete GC runs
- policy timeout count

Alert routing and SLO interpretation are defined in
`docs/68-slo-sli-alerting.md`.

## Incident Response

Default incident flow:

1. Classify impact from readiness, ops summary, logs, and customer reports.
2. Protect traffic if readiness is `not_ready`.
3. Check Postgres and object storage before optional dependencies.
4. Check identity trust-material status for auth failures.
5. Inspect worker backlog and failed search-job growth.
6. Capture a support bundle if the incident is not immediately understood.
7. Communicate rollback, failover, or repair decisions through the assigned
   incident channel.
8. Do not declare recovery until readiness and backlog signals stabilize.

Collect diagnostics:

```bash
API_URL=https://artifortress.example.com \
ADMIN_TOKEN=<admin-token> \
scripts/support-bundle.sh
```

Use `docs/66-diagnostic-error-catalog.md` for symptom-to-action mapping and
`docs/67-support-intake-and-escalation.md` for severity handling.

## Weekly And Release Cadence

Weekly:

- run `make phase6-drill`
- review SLO and alert events
- review privileged audit samples
- review active legal holds and governance approvals
- review PAT inventory and expiring credentials
- confirm support-bundle collection still redacts expected secrets

Before a release candidate:

- run `make verify-enterprise`
- refresh backup/restore, reliability, upgrade, search, and performance reports
- run production preflight for the target environment
- confirm release-candidate checklist evidence in
  `docs/61-release-candidate-signoff.md`
- reject promotion if provenance, backup/restore, upgrade, or P0 security gates
  are incomplete

## Offboarding Checklist

When an administrator, service account, or tenant subject is offboarded:

1. Revoke subject PATs.
2. Remove or narrow tenant role bindings.
3. Remove or narrow repository role bindings.
4. Confirm OIDC or SAML group mappings no longer grant access.
5. Review recent audit entries for the subject.
6. Preserve required compliance evidence.
7. Confirm no legal hold or governance approval ownership is left unresolved.

Tenant lifecycle workflow remains a separate P1 board item. Until that ticket is
closed, use this checklist plus organization-specific retention procedures.

## Supported References

- `docs/25-upgrade-rollback-runbook.md`
- `docs/29-deployment-howto-production.md`
- `docs/30-deployment-config-reference.md`
- `docs/31-operations-howto.md`
- `docs/37-phase7-runbook.md`
- `docs/38-phase7-rollout-gates.md`
- `docs/42-operations-dashboard-and-drill-bundle.md`
- `docs/49-compliance-evidence-and-legal-holds.md`
- `docs/50-search-assurance.md`
- `docs/59-enterprise-product-envelope.md`
- `docs/60-versioning-support-policy.md`
- `docs/61-release-candidate-signoff.md`
- `docs/65-support-bundle-workflow.md`
- `docs/66-diagnostic-error-catalog.md`
- `docs/67-support-intake-and-escalation.md`
- `docs/68-slo-sli-alerting.md`
- `docs/70-production-preflight.md`
