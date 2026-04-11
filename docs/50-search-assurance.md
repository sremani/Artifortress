# Search Assurance

This tranche closes the first three search-maturity tickets:
- `ER-4-01` search relevance/ranking maturity and deterministic query semantics
- `ER-4-02` search rebuild control plane: pause/resume/cancel/status
- `ER-4-03` search lag, freshness, and stuck-job observability

## Query Semantics

Search query serving now uses a deterministic ranking model for package lookups:
- exact `package_name` matches rank first
- exact `package_namespace` matches rank second
- prefix `package_name` matches rank third
- prefix `package_namespace` matches rank fourth
- text-search rank is used after the exact/prefix classes
- final ordering uses publish/index recency and stable package/version tiebreaks

This gives operators and tenants a documented search contract for common package-name
queries rather than relying on opaque full-text ranking alone.

## Search Control Plane

Tenant-scoped search controls are now persisted in `tenant_search_controls` and are
honored by both the API and the worker:
- `POST /v1/admin/search/pause` pauses new search outbox and job claiming for a tenant
- `POST /v1/admin/search/resume` resumes background search processing
- `POST /v1/admin/search/cancel` cancels queued or failed jobs for a tenant
- `GET /v1/admin/search/status` reports pause state, backlog counts, freshness lag, and
  stale processing job counts

The rebuild endpoint fails closed while a tenant is paused. Operators can pause search
for maintenance windows without stopping the rest of the control plane.

## Observability

Search status now exposes the signals needed to distinguish healthy eventual
consistency from a stuck read model:
- pending, processing, failed, completed, and cancelled job counts
- stale `processing` jobs based on lease age
- stale document counts where published versions are newer than indexed documents
- worst-case freshness lag in seconds
- latest indexed timestamp for the tenant

These fields are available through `GET /v1/admin/search/status` and are intended to
back dashboards and alert thresholds.

## Validation

The tranche is covered by integration tests for:
- deterministic exact-vs-prefix package ranking
- pause/resume/cancel control behavior across API and worker sweeps
- stale-processing and freshness-lag status reporting

Local validation:
- `dotnet build Artifortress.sln -c Debug`
- `make test`
- `make test-integration`
