# Artifortress Current State

Last updated: 2026-02-13

## 1. What Is Implemented

Runtime and tooling:
- `.NET SDK 10.0.102`, `net10.0`, `LangVersion=10.0`.
- Local dependency stack via `docker-compose` and `make` targets.
- SQL migration workflow with `schema_migrations` tracking.

API control-plane features:
- PAT issuance with TTL bounds and scope parsing.
- PAT revocation and active-token validation (revoked/expired denied).
- OIDC bearer-token validation path (HS256 + JWKS/RS256, issuer/audience checks, claim-role mapping policy) behind configuration gate.
- SAML metadata + ACS assertion exchange path with issuer/audience checks and claim-role mapping policy.
- Repo CRUD with repo-scoped authorization checks.
- Role binding upsert/list for subject-role assignment per repo.
- Audit persistence for privileged operations.
- Constant-time bootstrap token comparison.

Persistence:
- Core tables from `0001_init.sql` plus identity/RBAC tables from `0002_phase1_identity_and_rbac.sql`.
- Upload-session and publish/policy migrations from `0003_phase2_upload_sessions.sql`, `0004_phase3_publish_guardrails.sql`, `0005_phase3_published_immutability_hardening.sql`, and `0006_phase4_policy_search_quarantine_scaffold.sql`.
- Manifest persistence migration `0007_phase3_manifest_persistence.sql`.
- Phase 5 lifecycle migration `0008_phase5_tombstones_gc_reconcile.sql`.
- Post-GA search read-model migration `0009_post_ga_search_read_model.sql`.
- Token hash persistence only; plaintext token is response-only at issuance time.

## 2. API Endpoints (Implemented)

- `GET /`
- `GET /health/live`
- `GET /health/ready`
- `GET /v1/auth/whoami`
- `GET /v1/auth/saml/metadata`
- `POST /v1/auth/saml/acs`
- `POST /v1/auth/pats`
- `POST /v1/auth/pats/revoke`
- `GET /v1/repos`
- `GET /v1/repos/{repoKey}`
- `POST /v1/repos`
- `DELETE /v1/repos/{repoKey}`
- `PUT /v1/repos/{repoKey}/bindings/{subject}`
- `GET /v1/repos/{repoKey}/bindings`
- `POST /v1/repos/{repoKey}/packages/versions/drafts`
- `POST /v1/repos/{repoKey}/packages/versions/{versionId}/entries`
- `PUT /v1/repos/{repoKey}/packages/versions/{versionId}/manifest`
- `GET /v1/repos/{repoKey}/packages/versions/{versionId}/manifest`
- `POST /v1/repos/{repoKey}/packages/versions/{versionId}/publish`
- `POST /v1/repos/{repoKey}/packages/versions/{versionId}/tombstone`
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
- `GET /v1/repos/{repoKey}/search/packages`
- `POST /v1/admin/gc/runs`
- `POST /v1/admin/search/rebuild`
- `GET /v1/admin/ops/summary`
- `GET /v1/admin/reconcile/blobs`
- `GET /v1/audit`

## 3. Verification Status

Automated checks currently passing:
- `make format`.
- `make test` (non-integration filter) with `106` passing tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --filter "Category=Integration" -v minimal` with `86` passing integration tests.
- `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj -v minimal` with `192` passing tests.
- property-based test suite expansion in `tests/Artifortress.Domain.Tests/PropertyTests.fs`:
  - `91` FsCheck properties across domain, lifecycle/policy request validation, API, object storage config, and extracted worker internals (three extraction waves + post-GA search validation helpers).
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
- `scripts/phase3-demo.sh`
- `docs/19-phase3-runbook.md`
- `scripts/phase4-demo.sh`
- `docs/16-phase4-runbook.md`
- `scripts/phase5-demo.sh`
- `docs/21-phase5-runbook.md`
- `scripts/phase6-drill.sh`
- `scripts/phase6-demo.sh`
- `docs/23-phase6-runbook.md`
- `scripts/phase7-demo.sh`
- `docs/37-phase7-runbook.md`
- `docs/24-security-review-closure.md`
- `docs/25-upgrade-rollback-runbook.md`
- deployment docs:
  - `docs/26-deployment-plan.md`
  - `docs/27-deployment-architecture.md`
  - `docs/28-deployment-howto-staging.md`
  - `docs/29-deployment-howto-production.md`
  - `docs/30-deployment-config-reference.md`
  - `docs/31-operations-howto.md`
- mutation track assets:
  - `tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj`
  - `tools/Artifortress.MutationTrack/Program.fs`
  - `patches/stryker-net/0001-mut06-fsharp-analyzer-pipeline.patch`
  - `patches/stryker-net/0002-mut07a-fsharp-mutator-registration.patch`
  - `patches/stryker-net/0003-mut07b-fsharp-operator-candidate-planner.patch`
  - `patches/stryker-net/0004-mut08a-fsharp-source-span-mapper.patch`
  - `patches/stryker-net/0005-mut08b-lexical-safety-guards.patch`
  - `patches/stryker-net/0006-mut07b-fsharp-mutant-emission-quarantine.patch`
  - `patches/stryker-net/0007-mut07b-fsharp-path-selection.patch`
  - `scripts/mutation-fsharp-spike.sh`
  - `scripts/mutation-fsharp-native-run.sh`
  - `scripts/mutation-fsharp-native-score.sh`
  - `scripts/mutation-fsharp-native-trend.sh`
  - `scripts/mutation-fsharp-native-burnin.sh`
  - `scripts/mutation-trackb-bootstrap.sh`
  - `scripts/mutation-trackb-build.sh`
  - `scripts/mutation-trackb-spike.sh`
  - `scripts/mutation-trackb-assert.sh`
  - `scripts/mutation-trackb-compile-validate.sh`
  - `.github/workflows/mutation-track.yml`
  - `docs/32-fsharp-mutation-track-tickets.md`
  - `docs/33-fsharp-mutation-trackb-plan.md`
  - `docs/34-fsharp-native-mutation-tickets.md`
  - `docs/35-native-mutation-gate-promotion.md`
  - `docs/reports/mutation-spike-fsharp-latest.md`
  - `docs/reports/mutation-track-latest.md`
  - `docs/reports/mutation-native-fsharp-latest.md`
  - `docs/reports/mutation-native-score-latest.md`
  - `docs/reports/mutation-native-score-history-latest.md`
  - `docs/reports/mutation-native-burnin-latest.md`
  - `docs/reports/mutation-trackb-mut06-latest.md`
  - `docs/reports/mutation-trackb-mut07c-compile-validation.md`

## 4. Known Gaps vs Target Architecture

Not implemented yet:
- Search read-model relevance ranking tuning and richer query semantics beyond current deterministic baseline.
- OIDC remote JWKS fetch/refresh and key rollover automation beyond static `Auth__Oidc__JwksJson`.
- SAML signed-assertion cryptographic validation against IdP metadata (current path validates assertion structure/issuer/audience/claims).

## 5. Next Delivery Focus

- Phase 6 is complete with the following hardening capabilities:
  - dependency-backed readiness checks (`/health/ready`) for Postgres + object storage.
  - admin operations summary endpoint:
    - `GET /v1/admin/ops/summary`
  - operations-summary audit action:
    - `ops.summary.read`
  - backup/restore and drill tooling:
    - `scripts/db-backup.sh`
    - `scripts/db-restore.sh`
    - `scripts/phase6-drill.sh`
    - `scripts/phase6-demo.sh`
  - Phase 6 runbooks and closure docs:
    - `docs/22-phase6-implementation-tickets.md`
    - `docs/23-phase6-runbook.md`
    - `docs/24-security-review-closure.md`
    - `docs/25-upgrade-rollback-runbook.md`
- Next implementation targets:
  - Phase 7 post-completion hardening:
    - OIDC remote JWKS refresh and operational key-rotation automation.
    - SAML signed-assertion cryptographic validation path.
    - ticket board: `docs/36-phase7-identity-integration-tickets.md`.
  - search read-model maturity wave 2:
    - relevance/ranking tuning on `search_documents`.
    - incremental rebuild controls and operational rebuild runbook hardening.
  - F# native mutation finish plan (runtime lane + non-blocking CI + score/trend/burn-in policy are active; merge-gate promotion remains intentionally partial until the 7-run burn-in streak is satisfied).
  - continue expanding property-based and integration stress coverage around lifecycle and policy/search paths.
  - latest search maturity expansion (wave 8):
    - `P8-01 search query endpoint enforces authz and returns indexed published versions`
    - `P8-02 search query excludes quarantined and tombstoned versions while allowing released versions`
    - `P8-03 admin rebuild endpoint enqueues published versions and backfills searchable documents`
    - worker search processor now upserts `search_documents` projections for published versions.
