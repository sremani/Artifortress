# Artifortress

Artifortress is an artifact repository focused on correctness, integrity, and operational simplicity.

## Current Status

- Runtime baseline: `.NET SDK 10.0.102`, F# projects on `net10.0` and `LangVersion=10.0`.
- Phase 0: complete.
- Phase 1: complete.
- Phase 2: complete through P2-10 (upload/download APIs + verification + coverage + throughput baseline + demo/runbook).
- Phase 3: complete through P3-10 (artifact entry + manifest APIs, atomic publish, outbox emission, authz/audit coverage, integration tests, demo/runbook).
- Phase 4: implemented through P4-10 (policy/quarantine/search scaffolding + quarantine-aware read-path + authz/audit + fail-closed timeout semantics + deny/search-fallback integration coverage + demo/runbook).
- Phase 5: complete through P5-08 (tombstone lifecycle + GC dry-run/execute + reconcile summary + admin authz/audit + demo/runbook).
- Phase 6: complete through P6-10 (dependency-backed readiness, ops summary, backup/restore + DR drill, security closure, upgrade/rollback runbooks, GA demo/report).
- Worker PBT extraction waves W-PBT-01 through W-PBT-15: complete (pure helper extraction + property-based coverage expansion).
- Current build-out focus: post-Phase 7 hardening (OIDC JWKS refresh automation + signed SAML assertion cryptographic verification), search read-model serving maturity, and F# native mutation hardening (native runtime lane + non-blocking CI lane + score threshold reporting are active; next step is merge-gate promotion policy).

## Implemented Today

- Control plane:
  - PAT issue/revoke + bearer validation.
  - OIDC bearer-token validation path (HS256 + JWKS/RS256 + claim-role mapping) behind config gate.
  - SAML metadata + ACS assertion exchange with issuer/audience checks and claim-role mapping.
  - Repository CRUD (local/remote/virtual).
  - Repo role bindings.
  - Tenant-scoped audit query endpoint.
- Data plane (Phase 2):
  - Upload session create + multipart lifecycle (`parts`, `complete`, `abort`).
  - Commit-time digest/length verification with deterministic mismatch response.
  - Dedupe-by-digest fast path.
  - Blob download with range support, repository-scoped visibility enforcement, and quarantine/reject blocking (`423 quarantined_blob`).
  - Audit actions for upload lifecycle operations.
- Lifecycle operations (Phase 5):
  - Version tombstone API with retention windows.
  - Admin GC run API with safe dry-run mode and execute hard-delete mode.
  - Admin reconcile API for metadata/blob drift summary.
- Phase 6 hardening:
  - Readiness endpoint checks actual dependencies (Postgres + object storage) and returns deterministic non-ready responses.
  - Admin operations summary endpoint for backlog and lifecycle health posture.
  - Backup/restore scripts and DR drill automation with markdown report artifacts.
- Hardening updates:
  - Constant-time bootstrap token comparison.
  - Best-effort multipart abort on upload-session create race/failure paths.
  - Null-safe scope/role parsing guards in domain layer.
- Persistence:
  - Schema migrations in `db/migrations/0001_init.sql`, `db/migrations/0002_phase1_identity_and_rbac.sql`, `db/migrations/0003_phase2_upload_sessions.sql`, `db/migrations/0004_phase3_publish_guardrails.sql`, `db/migrations/0005_phase3_published_immutability_hardening.sql`, `db/migrations/0006_phase4_policy_search_quarantine_scaffold.sql`, `db/migrations/0007_phase3_manifest_persistence.sql`, and `db/migrations/0008_phase5_tombstones_gc_reconcile.sql`.
- Test coverage:
  - Domain unit tests.
  - API integration tests across authz, upload lifecycle, commit verification, dedupe, range behavior, and audit action matrix.
  - Property-based tests via FsCheck (`tests/Artifortress.Domain.Tests/PropertyTests.fs`), including extracted worker internals.
  - Latest local verification: `make format` and `make test` passing.

## Quick Start

Prerequisites:
- .NET SDK 10.0.102+
- Docker Desktop / Docker Engine with Compose
- GNU Make

Bootstrap and validate:
```bash
make dev-up
make storage-bootstrap
make db-migrate
make build
make test
make format
```

Run Phase 1 demo flow:
```bash
make phase1-demo
```

Run Phase 2 demo flow:
```bash
make phase2-demo
```

Run Phase 2 throughput baseline:
```bash
make phase2-load
```

Run Phase 4 demo flow:
```bash
make phase4-demo
```

Run Phase 5 demo flow:
```bash
make phase5-demo
```

Run Phase 6 readiness demo:
```bash
make phase6-demo
```

Run Phase 7 identity demo:
```bash
make phase7-demo
```

Run mutation track workflows:
```bash
make mutation-spike
make mutation-track
make mutation-fsharp-native
make mutation-fsharp-native-score
make mutation-fsharp-native-trend
make mutation-fsharp-native-burnin
make mutation-trackb-spike
make mutation-trackb-assert
make mutation-trackb-compile-validate
```

## API Surface (Current)

- `GET /health/live`
- `GET /health/ready`
- `POST /v1/auth/pats`
- `POST /v1/auth/pats/revoke`
- `GET /v1/auth/whoami`
- `GET /v1/repos`
- `GET /v1/repos/{repoKey}`
- `POST /v1/repos`
- `DELETE /v1/repos/{repoKey}`
- `PUT /v1/repos/{repoKey}/bindings/{subject}`
- `GET /v1/repos/{repoKey}/bindings`
- `POST /v1/repos/{repoKey}/uploads`
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
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/parts`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/complete`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/abort`
- `POST /v1/repos/{repoKey}/uploads/{uploadId}/commit`
- `GET /v1/repos/{repoKey}/blobs/{digest}`
- `POST /v1/admin/gc/runs`
- `GET /v1/admin/ops/summary`
- `GET /v1/admin/reconcile/blobs`
- `GET /v1/audit`

## Repository Layout

- `src/Artifortress.Domain`: domain primitives and authorization invariants.
- `src/Artifortress.Api`: HTTP API with Postgres-backed control plane and Phase 2 upload/download data plane.
- `src/Artifortress.Worker`: worker outbox/job sweep runtime plus extracted pure helper modules for deterministic behavior and PBT.
- `tests/Artifortress.Domain.Tests`: unit + integration tests.
- `db/migrations`: SQL schema migrations.
- `scripts`: migration, smoke, and demo scripts.
- `.github/workflows/ci.yml`: restore/build/test/format CI workflow.
- `.github/workflows/mutation-track.yml`: non-blocking nightly/manual mutation lane for Track B artifacts.
- `deploy/phase6-alert-thresholds.yaml`: Phase 6 SLO/alert threshold reference for operations.
- `deploy/*.env.example`: staging/production API and worker environment templates.

## Documentation Map

- `Architecture.md`: top-level architecture snapshot (implemented vs planned).
- `docs/01-system-architecture.md`: detailed architecture and invariants.
- `docs/05-implementation-plan.md`: phased roadmap with status.
- `docs/07-delivery-walkthrough-plan.md`: execution and demo sequencing.
- `docs/08-phase1-implementation-tickets.md`: completed Phase 1 ticket log.
- `docs/09-phase1-runbook.md`: executable Phase 1 demonstration runbook.
- `docs/10-current-state.md`: implementation inventory and known gaps.
- `docs/11-phase2-implementation-tickets.md`: active Phase 2 ticket board and acceptance criteria.
- `docs/12-phase3-implementation-tickets.md`: active Phase 3 ticket board and acceptance criteria.
- `docs/13-phase4-implementation-tickets.md`: active Phase 4 ticket board and acceptance criteria.
- `docs/14-worker-pbt-extraction-tickets.md`: worker extraction + property-based test ticket board and implementation notes.
- `docs/15-change-summary.md`: consolidated summary of recent hardening and extraction changes.
- `docs/16-phase4-runbook.md`: executable Phase 4 policy/quarantine/search demo runbook.
- `docs/17-phase2-runbook.md`: executable Phase 2 upload/download demo + load baseline runbook.
- `docs/18-phase2-throughput-baseline.md`: latest recorded Phase 2 throughput baseline and target outcomes.
- `docs/19-phase3-runbook.md`: executable Phase 3 draft/manifest/publish demo runbook.
- `docs/20-phase5-implementation-tickets.md`: active Phase 5 ticket board and acceptance criteria.
- `docs/21-phase5-runbook.md`: executable Phase 5 tombstone/GC/reconcile demo runbook.
- `docs/22-phase6-implementation-tickets.md`: active Phase 6 ticket board and acceptance criteria.
- `docs/23-phase6-runbook.md`: executable Phase 6 hardening/GA demo runbook.
- `docs/24-security-review-closure.md`: Phase 6 security closure and launch blocker status.
- `docs/25-upgrade-rollback-runbook.md`: deterministic production upgrade and rollback procedure.
- `docs/26-deployment-plan.md`: phased deployment strategy and ownership model.
- `docs/27-deployment-architecture.md`: deployment topology, health model, scaling, and failure domains.
- `docs/28-deployment-howto-staging.md`: staging deployment runbook with step-by-step commands.
- `docs/29-deployment-howto-production.md`: production rollout/rollback execution runbook.
- `docs/30-deployment-config-reference.md`: API/worker/script environment variable reference.
- `docs/31-operations-howto.md`: day-2 operations, incident handling, and drill cadence guide.
- `docs/32-fsharp-mutation-track-tickets.md`: Track B mutation ticket board and current status.
- `docs/33-fsharp-mutation-trackb-plan.md`: Track B strategy for F# mutation support via upstream/fork work.
- `docs/34-fsharp-native-mutation-tickets.md`: F#-first native mutation finish plan and ticket board.
- `docs/35-native-mutation-gate-promotion.md`: promotion criteria and rollout guide for making native mutation lane blocking.
- `docs/36-phase7-identity-integration-tickets.md`: Phase 7 identity federation ticket board and implementation notes.
- `docs/37-phase7-runbook.md`: executable Phase 7 identity runbook (OIDC + SAML) and troubleshooting guide.
- `docs/38-phase7-rollout-gates.md`: Phase 7 rollout gates and fallback-control checklist.
- `docs/reports/phase2-load-baseline-latest.md`: generated raw baseline report from `make phase2-load`.
- `docs/reports/mutation-spike-fsharp-latest.md`: latest F# mutation feasibility report artifact.
- `docs/reports/mutation-track-latest.md`: latest wrapper default mutation run report artifact.
- `docs/reports/mutation-native-fsharp-latest.md`: latest native F# runtime mutation report artifact.
- `docs/reports/mutation-native-score-latest.md`: latest native mutation score and threshold evaluation report.
- `docs/reports/mutation-native-score-history-latest.md`: latest rolling score trend view sourced from persisted history CSV.
- `docs/reports/mutation-native-burnin-latest.md`: latest burn-in readiness report (streak-based gate promotion signal).
- `docs/reports/mutation-trackb-mut06-latest.md`: latest patched-fork Track B mutation run report artifact (currently shows quarantined emitted F# mutants).
- `docs/reports/mutation-trackb-mut07c-compile-validation.md`: latest compile-validation artifact for sampled F# mutant rewrites.

## ADRs

Use `docs/adrs/0000-adr-template.md` for new architectural decisions.
