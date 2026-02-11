# Artifortress Current State

Last updated: 2026-02-11

## 1. What Is Implemented

Runtime and tooling:
- `.NET SDK 10.0.102`, `net10.0`, `LangVersion=10.0`.
- Local dependency stack via `docker-compose` and `make` targets.
- SQL migration workflow with `schema_migrations` tracking.

API control-plane features:
- PAT issuance with TTL bounds and scope parsing.
- PAT revocation and active-token validation (revoked/expired denied).
- Repo CRUD with repo-scoped authorization checks.
- Role binding upsert/list for subject-role assignment per repo.
- Audit persistence for privileged operations.
- Constant-time bootstrap token comparison.

Persistence:
- Core tables from `0001_init.sql` plus identity/RBAC tables from `0002_phase1_identity_and_rbac.sql`.
- Upload-session and publish/policy migrations from `0003_phase2_upload_sessions.sql`, `0004_phase3_publish_guardrails.sql`, `0005_phase3_published_immutability_hardening.sql`, and `0006_phase4_policy_search_quarantine_scaffold.sql`.
- Token hash persistence only; plaintext token is response-only at issuance time.

## 2. API Endpoints (Implemented)

- `GET /`
- `GET /health/live`
- `GET /health/ready`
- `GET /v1/auth/whoami`
- `POST /v1/auth/pats`
- `POST /v1/auth/pats/revoke`
- `GET /v1/repos`
- `GET /v1/repos/{repoKey}`
- `POST /v1/repos`
- `DELETE /v1/repos/{repoKey}`
- `PUT /v1/repos/{repoKey}/bindings/{subject}`
- `GET /v1/repos/{repoKey}/bindings`
- `POST /v1/repos/{repoKey}/packages/versions/drafts`
- `POST /v1/repos/{repoKey}/policy/evaluations`
- `GET /v1/repos/{repoKey}/quarantine`
- `GET /v1/repos/{repoKey}/quarantine/{quarantineId}`
- `POST /v1/repos/{repoKey}/quarantine/{quarantineId}/release`
- `POST /v1/repos/{repoKey}/quarantine/{quarantineId}/reject`
- `POST /v1/repos/{repoKey}/uploads`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/parts`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/complete`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/abort`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/commit`
- `GET /v1/repos/{repoKey}/blobs/{digest}`
- `GET /v1/audit`

## 3. Verification Status

Automated checks currently passing:
- `make format`.
- `make test` (non-integration filter) with `84` passing tests.
- `make test-integration` with `40` passing integration tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -nologo` with `124` passing tests.
- property-based test suite expansion in `tests/Artifortress.Domain.Tests/PropertyTests.fs`:
  - `75` FsCheck properties across domain, API, object storage config, and extracted worker internals (three extraction waves).
- `make phase2-load` baseline run:
  - upload throughput: `1.22 MiB/s` (`4.89 req/s`) with `12` upload iterations at `262144` bytes/object.
  - download throughput: `7.25 MiB/s` (`28.99 req/s`) with `36` download iterations.
  - generated report: `docs/reports/phase2-load-baseline-latest.md`.
- `make phase2-demo` run:
  - validated upload lifecycle, dedupe path, deterministic mismatch error, full/ranged download checks, and upload audit action presence.

Environment note:
- Integration checks require Postgres (`localhost:5432`) and MinIO (`localhost:9000`) availability.

Demonstration assets:
- `scripts/phase1-demo.sh`
- `docs/09-phase1-runbook.md`
- `scripts/phase2-demo.sh`
- `scripts/phase2-load.sh`
- `docs/17-phase2-runbook.md`
- `docs/18-phase2-throughput-baseline.md`
- `scripts/phase4-demo.sh`
- `docs/16-phase4-runbook.md`

## 4. Known Gaps vs Target Architecture

Not implemented yet:
- Atomic draft/publish package version workflow.
- Transactional outbox worker processing.
- Search indexing read-model query-serving integration beyond the current queue/job scaffolding.
- OIDC/SAML identity provider integration.

## 5. Next Delivery Focus

- Phase 2 implemented work:
  - `db/migrations/0003_phase2_upload_sessions.sql` for session persistence.
  - object storage client module in `src/Artifortress.Api/ObjectStorage.fs`.
  - upload session create API: `POST /v1/repos/{repoKey}/uploads`.
  - multipart lifecycle APIs:
    - `POST /v1/repos/{repoKey}/uploads/{uploadId}/parts`
    - `POST /v1/repos/{repoKey}/uploads/{uploadId}/complete`
    - `POST /v1/repos/{repoKey}/uploads/{uploadId}/abort`
  - commit verification API:
    - `POST /v1/repos/{repoKey}/uploads/{uploadId}/commit`
  - blob download API:
    - `GET /v1/repos/{repoKey}/blobs/{digest}` (single-range support, scoped to blobs committed in the requested repository)
  - upload audit coverage:
    - session create, part URL issuance, complete, abort, commit success, and commit verification failure.
  - reliability hardening:
    - best-effort object-store multipart abort on upload-session create race/failure paths.
    - null-safe role and scope parsing in domain layer.
    - migration script SQL quoting hardening for version tracking.
  - Phase 3 kickoff:
    - publish guardrail migration `db/migrations/0004_phase3_publish_guardrails.sql`.
    - published-immutability hardening migration `db/migrations/0005_phase3_published_immutability_hardening.sql`.
    - published-version immutability trigger for `package_versions`.
    - additional indexes for publish-state reads and pending outbox scans.
    - draft version create API (`POST /v1/repos/{repoKey}/packages/versions/drafts`) with idempotent draft reuse semantics.
  - Phase 4 kickoff:
    - policy/quarantine/search scaffold migration `db/migrations/0006_phase4_policy_search_quarantine_scaffold.sql`.
    - policy decision history table (`policy_evaluations`).
    - quarantine workflow table (`quarantine_items`).
    - search indexing job queue table (`search_index_jobs`).
    - policy evaluation API (`POST /v1/repos/{repoKey}/policy/evaluations`) with deterministic `allow|deny|quarantine` outcomes.
    - quarantine-item upsert side effect for `quarantine` decisions.
    - policy evaluation audit action: `policy.evaluated`.
    - quarantine state APIs for list/get/release/reject.
    - quarantine resolution audit actions:
      - `quarantine.released`
      - `quarantine.rejected`
    - worker outbox producer for `version.published` events to enqueue `search_index_jobs`.
    - idempotent enqueue via `(tenant_id, version_id)` upsert for search-index jobs.
    - worker search-index job processor sweep with claim/process/complete/fail transitions.
    - bounded retry semantics for failed search-index jobs (attempt caps + backoff).
    - worker pure-helper extraction for deeper PBT coverage:
      - `src/Artifortress.Worker/WorkerInternals.fs` with `WorkerOutboxParsing`, `WorkerRetryPolicy`, and `WorkerEnvParsing`.
      - second-wave DB-adjacent helper extraction with `WorkerDbParameters`, `WorkerOutboxFlow`, and `WorkerJobFlow`.
      - third-wave SQL-row and reducer helper extraction with `WorkerDataShapes` and `WorkerSweepMetrics`.
      - worker runtime wiring updated to consume extracted helpers in sweep and config parsing paths.
      - worker extraction ticket board and implementation notes in `docs/14-worker-pbt-extraction-tickets.md`.
    - quarantine-aware blob download enforcement:
      - `GET /v1/repos/{repoKey}/blobs/{digest}` returns `423` (`quarantined_blob`) when digest is linked to a `quarantined` or `rejected` version.
      - blob reads resume after quarantine status transitions to `released`.
    - policy timeout fail-closed semantics:
      - policy evaluation returns deterministic `503` (`policy_timeout`) on timeout.
      - timeout path is fail-closed (`failClosed=true`) and does not persist policy/quarantine mutations.
      - timeout budget configurable via `Policy:EvaluationTimeoutMs` (default `250` ms).
    - expanded integration coverage:
      - expired-session rejection paths.
      - dedupe-path second-create behavior.
      - invalid range (`400`) and unsatisfiable range (`416`) responses.
      - authz rejection matrix across new upload/download endpoints.
      - audit action matrix assertions for upload lifecycle operations.
      - repo-scoped blob visibility regression test.
      - policy/quarantine authz matrix assertions on quarantine get/reject endpoints.
      - policy/quarantine mutation audit metadata assertions (actor/resource/details).
      - policy timeout fail-closed assertions across both `publish` and `promote` actions.
- Next implementation targets:
  - implement publish workflow APIs after draft create baseline (`P3-03` onward).
  - continue Phase 3 publish workflow implementation (`P3-03` onward).
