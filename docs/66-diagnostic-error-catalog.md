# Diagnostic Error Catalog

Last updated: 2026-04-27

## Purpose

This catalog maps common Artifortress symptoms and deterministic error states to
operator actions.

Use it with:

- `GET /health/ready`
- `GET /v1/admin/ops/summary`
- `docs/31-operations-howto.md`
- `docs/46-dependency-failure-mode-matrix.md`
- `scripts/support-bundle.sh`

## Readiness States

| State | Meaning | Operator Action |
|---|---|---|
| `ready` | Required dependencies are healthy. | Keep traffic enabled. Continue normal monitoring. |
| `degraded` | Required dependencies are healthy, but optional trust material or another warning state needs attention. | Inspect dependency details and trust-material status. Repair before staleness windows expire. |
| `not_ready` | A required dependency cannot safely satisfy its contract. | Keep instance out of traffic. Investigate Postgres and object storage first. |
| `not_configured` | Optional dependency is intentionally outside the active runtime contract. | Do not treat as an incident by itself. |

## Dependency Failures

| Signal | Likely Cause | Impact | Recovery |
|---|---|---|---|
| `postgres=not_ready` | database unavailable, credentials invalid, network failure, failover in progress | protected API paths fail; workers stop progressing | restore DB connectivity, verify migrations, re-check readiness |
| `object_storage=not_ready` | bucket unavailable, credentials invalid, endpoint unreachable | upload/download/blob flows fail | restore storage dependency, run smoke upload/download |
| `oidc_jwks=degraded` | remote JWKS refresh warning or stale remote with fallback | auth can continue within bounded fallback | repair IdP/JWKS endpoint before max staleness expires |
| `oidc_jwks=not_ready` | no fresh remote JWKS and no valid fallback | OIDC token validation fails closed | restore JWKS or deploy valid static fallback |
| `saml_metadata=degraded` | remote metadata refresh warning or stale remote with fallback | SAML can continue within bounded fallback | repair metadata endpoint before max staleness expires |
| `saml_metadata=not_ready` | no signing certs available or metadata stale without fallback | SAML assertion validation fails closed | restore metadata or deploy valid static fallback |

## API Error Conditions

| Error | Likely Cause | Operator Action |
|---|---|---|
| `401 unauthorized` | missing, expired, revoked, or invalid token | verify token issuer, expiry, revocation, and trust-material status |
| `403 forbidden` | principal lacks required tenant or repo role | inspect role bindings and claim-role mappings |
| `404 not_found` | resource absent or tenant/repo filter hides it | confirm tenant, repo key, and resource identifier |
| `409 conflict` | state transition or uniqueness conflict | inspect current resource state before retrying |
| `423 quarantined_blob` | blob/version is quarantined or rejected | inspect quarantine record and policy decision |
| `429 too_many_requests` | tenant admission limit reached | inspect tenant admission policy and reduce load or raise limit |
| `503 service_unavailable` | dependency not ready | inspect `/health/ready` and dependency matrix |

## Backlog And Worker Symptoms

| Signal | Likely Cause | Impact | Recovery |
|---|---|---|---|
| rising `pendingOutboxEvents` | worker down, DB pressure, batch too small | async work delayed | restart/scale worker, inspect DB latency, tune worker settings |
| growing `oldestPendingOutboxAgeSeconds` | sustained processing lag | delayed search/lifecycle side effects | scale workers or reduce write pressure |
| rising `pendingSearchJobs` | worker capacity below publish/rebuild load | stale search results | scale workers, pause rebuilds if needed |
| rising `failedSearchJobs` | malformed data, DB errors, worker defect | search drift risk | inspect worker logs, run reliability drill, collect support bundle |
| stale processing jobs | worker crash or lease expiry path active | delayed search jobs | verify lease reclaim with reliability drill |

## Lifecycle And Compliance Symptoms

| Signal | Likely Cause | Operator Action |
|---|---|---|
| incomplete GC run | GC interrupted or dependency failed | inspect GC run, dependency health, and retry after readiness is stable |
| legal hold blocks deletion | version is under active hold | confirm legal hold owner before release or deletion |
| reconcile drift | metadata/blob mismatch | freeze risky mutation paths, inspect reconcile output, validate backup/restore posture |
| compliance evidence digest mismatch | saved payload changed or header copied incorrectly | regenerate evidence pack and verify digest immediately |

## Release And Upgrade Symptoms

| Signal | Likely Cause | Operator Action |
|---|---|---|
| migration fails | incompatible schema state or DB permissions | stop rollout, keep old app version, restore if required |
| readiness fails after deploy | config or dependency mismatch | check env vars, DB, object storage, identity trust material |
| upgrade drill fails | unsupported baseline or migration regression | block release candidate and inspect drill report |
| provenance verification fails | wrong artifact, changed checksum, invalid signature | reject promotion until artifacts verify |

## Incident Data To Capture

Always capture:

- support bundle archive
- readiness output
- ops summary output
- release version or commit
- recent deployment/migration/config changes
- exact timestamps and timezone
- customer-visible impact
- rollback/failover actions already attempted

## Escalation Guidance

Escalate immediately for:

- production `not_ready`
- failed backup/restore evidence during a production incident
- suspected cross-tenant data exposure
- release provenance failure
- destructive lifecycle behavior that conflicts with legal hold
- repeated search/job failure growth after worker restart or scale-out
