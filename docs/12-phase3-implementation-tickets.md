# Phase 3 Implementation Tickets

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| P3-01 | DB migration for publish guardrails and indexes | Phase 2 baseline | done |
| P3-02 | Package + draft version create API | P3-01 | done |
| P3-03 | Draft artifact entry persistence API | P3-02 | done |
| P3-04 | Manifest persistence + validation scaffold | P3-02 | done |
| P3-05 | Atomic publish endpoint (single DB transaction) | P3-03, P3-04 | done |
| P3-06 | Publish outbox event emission (`version.published`) | P3-05 | done |
| P3-07 | AuthZ + audit coverage for publish workflow | P3-05 | done |
| P3-08 | Published-version immutability + deterministic conflicts | P3-01, P3-05 | done |
| P3-09 | Integration tests for atomic publish and rollback safety | P3-05, P3-06, P3-07, P3-08 | done |
| P3-10 | Phase 3 demo script + runbook updates | P3-09 | done |

## Current Implementation Notes (2026-02-11)

- Phase transition note:
  - Phase 4 kickoff board lives in `docs/13-phase4-implementation-tickets.md`.

- P3-01 completed:
  - Added migration `db/migrations/0004_phase3_publish_guardrails.sql`.
  - Added follow-up hardening migration `db/migrations/0005_phase3_published_immutability_hardening.sql`.
  - Added publish/read-path indexes for `package_versions` and pending `outbox_events`.
  - Added published-version immutability trigger (`trg_deny_published_version_mutation`) backed by `deny_published_version_mutation()`.
- P3-02 completed:
  - Added `POST /v1/repos/{repoKey}/packages/versions/drafts`.
  - Added repo-write authz checks, package upsert, draft version create-or-reuse behavior, and deterministic conflict for non-draft existing states.
  - Added integration test coverage for unauthorized/forbidden/authorized + idempotent draft reuse behavior.
  - Added integration test coverage for published-version immutability trigger enforcement.
  - Added integration test coverage for draft-create conflict when version already moved to `published`.
- P3-03 completed:
  - Added `POST /v1/repos/{repoKey}/packages/versions/{versionId}/entries`.
  - Added draft-state enforcement and deterministic checks for missing/uncommitted blob digests.
  - Added duplicate `relativePath` request validation.
- P3-04 completed:
  - Added migration `db/migrations/0007_phase3_manifest_persistence.sql` with `manifests` table and indexes.
  - Added `PUT/GET /v1/repos/{repoKey}/packages/versions/{versionId}/manifest`.
  - Added package-type manifest validation scaffold (`nuget`, `npm`, `maven`, generic object baseline).
- P3-05/P3-06 completed:
  - Added `POST /v1/repos/{repoKey}/packages/versions/{versionId}/publish`.
  - Publish path now enforces draft-only transition, entry/manifest preconditions, single-transaction publish, and in-transaction audit.
  - Emits `version.published` outbox event in the publish transaction with replay-safe dedupe behavior.
- P3-07/P3-08 completed:
  - Added role enforcement for all new publish-workflow endpoints (`write`, `read`, `promote`).
  - Added audit events:
    - `package.version.entries.upserted`
    - `package.version.manifest.upserted`
    - `package.version.published`
  - Added deterministic conflict responses for post-publish entry/manifest mutation attempts.
- P3-09 completed:
  - Added integration tests for:
    - artifact entry authz + blob existence checks
    - manifest validation + query path
    - atomic publish success + idempotent republish no duplicate outbox event
    - publish authz + audit matrix
    - rollback safety: publish failure does not persist partial publish/outbox state
- P3-10 completed:
  - Added `scripts/phase3-demo.sh`.
  - Added `docs/19-phase3-runbook.md`.
  - Added `make phase3-demo` target.
  - Latest local verification:
    - `make build`
    - `make test`
    - `dotnet test tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj --configuration Debug --no-build -v minimal --filter "Category=Integration"` (`47` passing integration tests)

## Ticket Details

## P3-01: DB migration for publish guardrails and indexes

Scope:
- Add indexes for publish/read and outbox delivery paths.
- Add DB trigger guardrail to prevent immutable-field mutations after publish.

Acceptance criteria:
- Migration applies cleanly from zero.
- Published row immutable-field updates fail deterministically.
- Query plan support exists for repo/state publish reads and pending outbox scans.

## P3-02: Package + draft version create API

Scope:
- Add endpoint(s) to create package coordinates and initialize draft versions.
- Enforce repo-scoped authorization and deterministic duplicate handling.

Acceptance criteria:
- Draft version create is idempotent or conflict-deterministic.
- Invalid package/version payloads fail with clear 4xx responses.

## P3-03: Draft artifact entry persistence API

Scope:
- Add endpoint(s) to attach artifact entries to draft versions.
- Verify referenced blob digests exist before persistence.

Acceptance criteria:
- Missing blob references are rejected deterministically.
- Duplicate relative paths are handled safely.

## P3-04: Manifest persistence + validation scaffold

Scope:
- Persist normalized manifest metadata for draft versions.
- Add validation scaffold per package type.

Acceptance criteria:
- Invalid manifests reject with clear error shape.
- Stored manifests are queryable by version.

## P3-05: Atomic publish endpoint (single DB transaction)

Scope:
- Publish draft to `published` in one transaction with all required writes.
- Assert all guards (blob existence, state transition legality) within the transaction.

Acceptance criteria:
- No partial publish state on failure.
- Published state transition is deterministic and race-safe.

## P3-06: Publish outbox event emission (`version.published`)

Scope:
- Emit outbox event in the same publish transaction.
- Include stable aggregate identity and payload shape.

Acceptance criteria:
- Event appears only when publish commit succeeds.
- No duplicate event on retried idempotent publish.

## P3-07: AuthZ + audit coverage for publish workflow

Scope:
- Enforce publish permissions for all new package/version mutation endpoints.
- Add audit records for draft create, entry updates, and publish outcomes.

Acceptance criteria:
- Unauthorized/forbidden responses are deterministic.
- Privileged publish-path mutations are auditable.

## P3-08: Published-version immutability + deterministic conflicts

Scope:
- Ensure immutable published rows cannot be modified outside legal transitions.
- Map DB conflict/guardrail errors to deterministic API responses.

Acceptance criteria:
- Illegal mutations return deterministic conflict responses.
- Immutability behavior is covered by tests.

## P3-09: Integration tests for atomic publish and rollback safety

Scope:
- Add integration coverage for draft->publish happy path and failure rollback paths.
- Validate outbox emission semantics and immutability post-publish.

Acceptance criteria:
- Failure path proves no partial publication persists.
- CI fails on atomicity regressions.

## P3-10: Phase 3 demo script + runbook updates

Scope:
- Add scripted Phase 3 walkthrough for draft, publish, audit, and outbox visibility.
- Document operational verification steps.

Acceptance criteria:
- One script demonstrates core Phase 3 flow.
- Runbook includes expected outputs and troubleshooting.
