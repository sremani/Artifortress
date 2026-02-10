# Phase 2 Implementation Tickets

Status key:
- `todo`: not started
- `in_progress`: currently being implemented
- `done`: implemented and validated in this repository
- `blocked`: cannot proceed until dependency/risk is resolved

## Ticket Board

| Ticket | Title | Depends On | Status |
|---|---|---|---|
| P2-01 | DB migration for upload sessions and critical indexes | Phase 1 complete | done |
| P2-02 | Object storage client for multipart upload + download streaming | P2-01 | done |
| P2-03 | Upload session create API (`POST /v1/repos/{repoKey}/uploads`) | P2-01, P2-02 | done |
| P2-04 | Multipart session lifecycle APIs (parts, complete, abort) | P2-03 | done |
| P2-05 | Commit endpoint with digest/size verification and dedupe | P2-03, P2-04 | done |
| P2-06 | Blob download endpoint with HTTP range support | P2-02 | done |
| P2-07 | AuthZ + audit coverage for all new data-plane endpoints | P2-03, P2-04, P2-05, P2-06 | done |
| P2-08 | Integration tests for upload/download correctness and failure paths | P2-05, P2-06, P2-07 | done |
| P2-09 | Phase 2 throughput baseline and load test report | P2-05, P2-06 | todo |
| P2-10 | Phase 2 demo script and runbook updates | P2-08, P2-09 | todo |

## Current Implementation Notes (2026-02-10)

- P2-01 completed:
  - Added migration `db/migrations/0003_phase2_upload_sessions.sql`.
  - Introduced `upload_sessions` state model aligned to `docs/04-state-machines.md`.
  - Added indexes for repository/state and session expiration scans.
- P2-02 completed:
  - Added `src/Artifortress.Api/ObjectStorage.fs` with typed storage errors and MinIO/S3 multipart + streaming operations.
  - Added config support for part URL TTL (`ObjectStorage:PresignPartTtlSeconds`).
  - Added MinIO-backed integration coverage in `tests/Artifortress.Domain.Tests/ObjectStorageTests.fs`.
- P2-03 completed:
  - Added `POST /v1/repos/{repoKey}/uploads`.
  - Added digest/length validation, repo write authz checks, dedupe fast-path, and session persistence.
  - Added integration coverage in `tests/Artifortress.Domain.Tests/ApiIntegrationTests.fs`.
- P2-04 completed:
  - Added `POST /v1/repos/{repoKey}/uploads/{uploadId}/parts`.
  - Added `POST /v1/repos/{repoKey}/uploads/{uploadId}/complete`.
  - Added `POST /v1/repos/{repoKey}/uploads/{uploadId}/abort`.
  - Enforced session-state guards and key transitions (`initiated -> parts_uploading -> pending_commit`, active -> `aborted`).
- P2-05 completed:
  - Added `POST /v1/repos/{repoKey}/uploads/{uploadId}/commit`.
  - Added digest+length verification against object store content at commit time.
  - Added deterministic mismatch response (`upload_verification_failed`) and abort-on-mismatch behavior.
- P2-06 completed:
  - Added `GET /v1/repos/{repoKey}/blobs/{digest}` with single-range support.
  - Added response semantics for full and ranged reads from object storage.
- P2-07 completed:
  - AuthZ enforcement is in place for upload and blob read endpoints.
  - Audit events added for session create, part URL issue, complete, abort, commit success, and commit verification failure.
  - Added integration coverage validating upload mutation audit emission.
- P2-08 completed:
  - Added integration coverage for upload create/authz, part URL issue, complete, abort, commit success, commit mismatch, and ranged/full blob download.
  - Added explicit expired-session behavior coverage.
  - Added explicit dedupe-path coverage for second upload with same digest.
  - Added invalid range and unsatisfiable range (`416`) coverage.
  - Added expanded authz rejection matrix across new upload/download endpoints.
  - Added upload lifecycle audit action matrix assertions.

## Ticket Details

## P2-01: DB migration for upload sessions and critical indexes

Scope:
- Add SQL migration for `upload_sessions`.
- Add status constraints matching upload state machine.
- Add indexes for repo/state lookup and expiry sweeps.

Acceptance criteria:
- Migration applies cleanly from zero.
- Session states enforce `initiated`, `parts_uploading`, `pending_commit`, `committed`, `aborted`.
- Indexes support repository/state and expiry-driven queries.

## P2-02: Object storage client for multipart upload + download streaming

Scope:
- Add S3-compatible storage abstraction used by API endpoints.
- Support multipart upload init, part URL signing, completion, and abort.
- Support blob read streams with range offsets.

Acceptance criteria:
- Storage client can operate against local MinIO dev environment.
- Failures return explicit typed errors for API mapping.

## P2-03: Upload session create API (`POST /v1/repos/{repoKey}/uploads`)

Scope:
- Create upload session with expected digest and expected length.
- Authorize via repo-scoped `write` role.
- Implement fast-path dedupe when digest already exists.

Acceptance criteria:
- Valid request returns upload session id and next action payload.
- Existing digest path avoids duplicate object write plan.

## P2-04: Multipart session lifecycle APIs (parts, complete, abort)

Scope:
- Add endpoints to request part instructions, complete part upload, and abort.
- Enforce legal transitions from `docs/04-state-machines.md`.

Acceptance criteria:
- Illegal transitions return deterministic 4xx errors.
- Aborted sessions are terminal and cannot resume.

## P2-05: Commit endpoint with digest/size verification and dedupe

Scope:
- Add commit endpoint that verifies final object digest and content length.
- On success, persist blob row and set session `committed`.
- On mismatch, set `aborted` and emit deterministic error shape.

Acceptance criteria:
- Committed sessions always have verified digest and size.
- Mismatch path is deterministic and auditable.

## P2-06: Blob download endpoint with HTTP range support

Scope:
- Add endpoint for downloading by digest.
- Implement `Range` request handling (`200`, `206`, `416`).
- Stream from object storage without buffering full object in memory.

Acceptance criteria:
- Full and ranged downloads return correct headers and byte boundaries.
- Download path enforces repo-scoped `read` authorization.

## P2-07: AuthZ + audit coverage for all new data-plane endpoints

Scope:
- Apply existing authn/authz policy checks to all upload/download routes.
- Write audit entries for privileged and failure paths.

Acceptance criteria:
- Unauthorized and forbidden calls are deterministic.
- Privileged mutation calls generate audit records.

## P2-08: Integration tests for upload/download correctness and failure paths

Scope:
- Add API integration tests for happy path and failure path.
- Cover mismatch, dedupe, expired/aborted sessions, and authz rejection.

Acceptance criteria:
- Test suite validates core Phase 2 invariants.
- Regressions fail in CI.

### P2-08 Sub-Task Board (2026-02-10)

| Sub-Task | Title | Status | Update |
|---|---|---|---|
| P2-08a | Upload lifecycle happy path (create -> parts -> complete -> commit) | done | Covered by complete+commit success integration tests. |
| P2-08b | Commit verification mismatch path | done | Deterministic `upload_verification_failed` path is tested. |
| P2-08c | Download correctness (full + ranged `206`) | done | Full-body and single-range responses are tested. |
| P2-08d | Terminal-state edge cases (`aborted`/`committed` behavior) | done | `aborted` is terminal for subsequent part upload; `committed` terminal behavior is exercised in lifecycle tests. |
| P2-08e | Expired-session behavior coverage | done | Expired sessions now covered for parts/complete/commit rejection paths. |
| P2-08f | Dedupe-path coverage | done | Second create with same digest/length is tested to return deduped committed flow. |
| P2-08g | AuthZ rejection matrix across new endpoints | done | Unauthorized+forbidden checks now cover parts/complete/abort/commit/download. |
| P2-08h | Download invalid-range and `416` behavior | done | Invalid range (`400`) and unsatisfiable range (`416`) are tested. |
| P2-08i | Audit event matrix assertions for new lifecycle actions | done | Assertions include create/part/complete/abort/commit/verification-failed actions. |

## P2-09: Phase 2 throughput baseline and load test report

Scope:
- Add a repeatable load test script for upload/download throughput.
- Capture baseline numbers and resource envelope in docs.

Acceptance criteria:
- Throughput target is defined and measured.
- Results are documented and reproducible locally.

## P2-10: Phase 2 demo script and runbook updates

Scope:
- Add scripted demo for upload, dedupe, checksum validation, and ranged download.
- Update runbook docs to execute demo end-to-end.

Acceptance criteria:
- A developer can run one script and observe all Phase 2 core behaviors.
- Runbook troubleshooting notes are present.
