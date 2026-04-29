# Tenant Isolation And Admission Controls

## Scope

This document closes the first enterprise multi-tenant tranche:
- `ER-2-01` formal tenant isolation invariants
- `ER-2-02` cross-tenant negative integration coverage
- `ER-2-03` tenant quotas and admission controls

## Isolation Invariants

Kublai enforces tenant boundaries with these invariants:

1. Every mutable domain aggregate is tenant-scoped.
   Tables for repositories, package versions, upload sessions, search jobs, search documents, quarantine state, audit records, and PAT policy records all carry `tenant_id`.

2. Authentication resolves a concrete tenant before authorization is applied.
   PAT authentication loads `tenant_id` from the stored token record. OIDC and SAML bootstrap flows resolve the configured tenant before issuing a principal.

3. Authorization is necessary but not sufficient.
   Repository scope checks decide whether a caller may act on a repo key. Data access then applies `tenant_id` filters so a token with a syntactically matching repo scope cannot cross tenant boundaries.

4. Shared identifiers do not imply shared visibility.
   The same `repo_key` may exist in multiple tenants. Search, repository reads, rebuild jobs, quarantine state, and audit reads must only surface rows where the caller principal and stored record have the same `tenant_id`.

5. Background work is tenant-owned.
   Search rebuild requests and queued search jobs are enqueued with `tenant_id` and processed only against rows in that same tenant.

## Admission Controls

Tenant admission policy is managed through:
- `GET /v1/admin/tenant-admission-policy`
- `PUT /v1/admin/tenant-admission-policy`

Policy fields:
- `maxLogicalStorageBytes`
- `maxConcurrentUploadSessions`
- `maxPendingSearchJobs`

Current enforcement points:
- upload session creation rejects requests when active upload sessions already meet the configured tenant cap
- search rebuild requests are capped by remaining pending-job capacity and return `429` when no tenant capacity remains
- tenant admission policy carries a logical storage limit field for staged rollout alongside the live concurrency and background-job controls

## Negative Coverage

Integration coverage now proves:
- the same repo key can exist in two tenants without search bleed
- admin actions against tenant A resources fail closed for tenant B principals
- search queries only return documents for the caller tenant even when repo scopes match
- tenant admission policies return deterministic `429` responses for over-limit upload and rebuild requests

## Operational Notes

- The default tenant admission policy is intentionally generous and should be tightened per hosted tenant.
- `429 too_many_requests` from upload or search rebuild APIs is a tenant-capacity signal, not an infrastructure outage.
- Policy updates are audited as `tenant.admission.policy.updated`.
