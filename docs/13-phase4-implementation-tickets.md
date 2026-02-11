# Phase 4 Implementation Tickets

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| P4-01 | DB migration scaffold for policy evaluation, quarantine, and search indexing queues | Phase 3 baseline | done |
| P4-02 | Policy evaluation API hook for publish/promotion decisions | P4-01 | done |
| P4-03 | Quarantine state API (list/get/release/reject) | P4-01, P4-02 | done |
| P4-04 | Search index job producer from outbox `version.published` events | P4-01 | done |
| P4-05 | Worker handler for search index job processing | P4-04 | done |
| P4-06 | Resolve/read-path behavior when version is quarantined | P4-03 | done |
| P4-07 | AuthZ + audit coverage for policy/quarantine APIs | P4-02, P4-03 | done |
| P4-08 | Policy timeout/fail-closed semantics with deterministic errors | P4-02 | done |
| P4-09 | Integration tests for deny/quarantine/search fallback behavior | P4-03, P4-05, P4-06, P4-08 | done |
| P4-10 | Phase 4 runbook and demo script updates | P4-09 | todo |

## Current Implementation Notes (2026-02-11)

- P4-01 completed:
  - Added migration `db/migrations/0006_phase4_policy_search_quarantine_scaffold.sql`.
  - Added `policy_evaluations` table + indexes for policy decision history and query patterns.
  - Added `quarantine_items` table + indexes for quarantine workflow status queries.
  - Added `search_index_jobs` table + pending-job index for asynchronous indexing workflows.
- P4-02 completed:
  - Added policy evaluation API endpoint:
    - `POST /v1/repos/{repoKey}/policy/evaluations`
  - Added deterministic policy decision handling for `publish`/`promote` actions with decision outcomes:
    - `allow`, `deny`, `quarantine`
  - Added transactional persistence for policy decisions in `policy_evaluations`.
  - Added quarantine upsert side effect (`quarantine_items`) when decision is `quarantine`.
  - Added audit action for policy evaluations:
    - `policy.evaluated`
  - Added integration tests for:
    - unauthorized/forbidden/authorized policy evaluation paths
    - default-allow decision behavior
    - quarantine decision persistence and quarantine-item creation
- P4-03 completed:
  - Added quarantine APIs:
    - `GET /v1/repos/{repoKey}/quarantine`
    - `GET /v1/repos/{repoKey}/quarantine/{quarantineId}`
    - `POST /v1/repos/{repoKey}/quarantine/{quarantineId}/release`
    - `POST /v1/repos/{repoKey}/quarantine/{quarantineId}/reject`
  - Added deterministic quarantine status filter validation (`quarantined|released|rejected`).
  - Added explicit transition rules:
    - only `quarantined -> released|rejected` is allowed
    - repeated resolution attempts return deterministic conflict
  - Added audit actions for resolution:
    - `quarantine.released`
    - `quarantine.rejected`
  - Added integration tests for authz, list/get, release/reject transitions, and audit assertions.
- P4-04 completed:
  - Added search-index outbox producer in worker module:
    - `Artifortress.Worker.SearchIndexOutboxProducer.runSweep`
  - Added outbox claim logic for pending `version.published` events using `FOR UPDATE SKIP LOCKED`.
  - Added idempotent search-job enqueue semantics:
    - `search_index_jobs` upsert by `(tenant_id, version_id)` to keep one pending job per version.
  - Added outbox delivery semantics:
    - successful enqueue marks source outbox event as delivered.
    - malformed events are requeued with delayed availability.
  - Added integration coverage for:
    - successful outbox->search-job enqueue + delivered mark.
    - malformed event requeue without job creation.
- P4-05 completed:
  - Added search-index job processor in worker module:
    - `Artifortress.Worker.SearchIndexJobProcessor.runSweep`
  - Added job claim/processing loop:
    - claim pending/failed jobs due for processing via `FOR UPDATE SKIP LOCKED`.
    - mark claimed jobs as `processing`.
    - complete jobs when target version is publish-ready.
    - fail/requeue jobs when target version is not publish-ready.
  - Added bounded retry semantics:
    - attempts increment on failures.
    - claim query enforces `attempts < maxAttempts`.
    - exponential backoff scheduling on failure.
  - Added integration coverage for:
    - successful completion path for published-version jobs.
    - failure + bounded retry behavior for unpublished-version jobs.
- P4-06 completed:
  - Added quarantine-aware blob download guard in API:
    - `GET /v1/repos/{repoKey}/blobs/{digest}`
  - Added deterministic blocked-read behavior:
    - returns `423` with `error=quarantined_blob` when digest is linked to a version with quarantine status `quarantined` or `rejected`.
    - allows reads after quarantine status transitions to `released`.
  - Added integration tests for:
    - blocked blob reads while quarantined.
    - unblock behavior after release.
    - continued blocked reads after reject.
- P4-07 completed:
  - Added integration authz matrix coverage for quarantine item get/reject paths:
    - unauthorized (`401`), forbidden (`403`), and authorized success paths.
  - Added integration audit metadata coverage for policy/quarantine mutations:
    - verifies `policy.evaluated` audit record actor/resource/details fields.
    - verifies `quarantine.released` audit record actor/resource/details fields.
- P4-08 completed:
  - Added policy-evaluation timeout gate with configurable timeout budget:
    - `Policy:EvaluationTimeoutMs` (default `250` ms).
  - Added deterministic fail-closed timeout response for policy evaluation uncertainty:
    - `503` with `error=policy_timeout`, `failClosed=true`, and `timeoutMs`.
  - Added timeout audit action:
    - `policy.timeout` with repo/version/action metadata.
  - Added integration tests that verify fail-closed behavior for both `publish` and `promote` actions:
    - no policy/quarantine persistence occurs on timeout paths.
- P4-09 completed:
  - Added integration test coverage for explicit `deny` policy decisions:
    - persisted policy evaluation decision is `deny`.
    - no quarantine item side effects for deny path.
  - Added integration test coverage for degraded search pipeline behavior:
    - malformed outbox event requeue + failed unpublished search job paths are exercised.
    - policy quarantine->release flow remains correct while degraded search events/jobs exist.

## Ticket Details

## P4-01: DB migration scaffold for policy evaluation, quarantine, and search indexing queues

Scope:
- Add schema scaffold needed by Phase 4 policy/search workflows.
- Add indexes aligned with expected read/write paths for policy decisions, quarantine operations, and indexing jobs.

Acceptance criteria:
- Migration applies cleanly from zero and from current Phase 3 baseline.
- Query/index support exists for quarantine status lists and pending search jobs.

## P4-02: Policy evaluation API hook for publish/promotion decisions

Scope:
- Add policy evaluation hook invoked during publish/promotion commands.
- Persist decision outcomes (`allow`, `deny`, `quarantine`) with structured reason payloads.

Acceptance criteria:
- Deny/quarantine decisions are deterministic and auditable.
- Policy decision records are queryable by repo/version/action.

## P4-03: Quarantine state API (list/get/release/reject)

Scope:
- Add APIs to inspect and resolve quarantine items.
- Support explicit release/reject actions with actor attribution.

Acceptance criteria:
- Quarantine operations are permission-gated and audited.
- Item status transitions are deterministic and validated.

## P4-04: Search index job producer from outbox `version.published` events

Scope:
- Transform publish outbox events into `search_index_jobs` enqueue actions.
- Ensure idempotent enqueue behavior by version identity.

Acceptance criteria:
- Publish event creates or updates a pending index job exactly once per version.
- Queue remains consistent under replay.

## P4-05: Worker handler for search index job processing

Scope:
- Implement worker loop/handler for job claim/process/ack/retry.
- Persist attempts and failure context.

Acceptance criteria:
- Pending jobs are processed to completed/failed states.
- Retry behavior is bounded and observable.

## P4-06: Resolve/read-path behavior when version is quarantined

Scope:
- Ensure quarantined versions are hidden or blocked from normal resolution paths.
- Return deterministic API errors for quarantined content access where applicable.

Acceptance criteria:
- Quarantined versions are not returned by standard resolve flows.
- Behavior is consistent across read endpoints.

## P4-07: AuthZ + audit coverage for policy/quarantine APIs

Scope:
- Enforce scoped permissions on new policy/quarantine endpoints.
- Emit audit events for decision and resolution actions.

Acceptance criteria:
- Unauthorized/forbidden responses are deterministic.
- Policy/quarantine mutations are auditable.

## P4-08: Policy timeout/fail-closed semantics with deterministic errors

Scope:
- Implement timeout handling for policy evaluation path.
- Enforce fail-closed behavior for publish/promotion operations.

Acceptance criteria:
- Timeout results in deterministic failure payload.
- No publish/promotion bypass occurs on policy uncertainty.

## P4-09: Integration tests for deny/quarantine/search fallback behavior

Scope:
- Add end-to-end tests for policy deny, quarantine, release, and search indexing behaviors.
- Include degraded index-path behavior expectations.

Acceptance criteria:
- Deny/quarantine scenarios are covered with deterministic assertions.
- Search unavailability does not break core correctness path.

## P4-10: Phase 4 runbook and demo script updates

Scope:
- Add Phase 4 demo script that exercises policy deny/quarantine/search paths.
- Update runbook with operational checks and troubleshooting.

Acceptance criteria:
- One script demonstrates the key Phase 4 workflow.
- Runbook includes expected outputs and recovery guidance.
