# Critical State Machines

The following state machines define legal transitions and fault handling for correctness-critical flows.

## 1. Upload State Machine

### States

- `initiated`
- `parts_uploading`
- `pending_commit`
- `committed`
- `aborted`

### Transition Table

| Current State | Command | Guard | Next State | Side Effects |
|---|---|---|---|---|
| initiated | start_parts_upload | session valid and not expired | parts_uploading | issue pre-signed part URLs |
| initiated | fast_path_dedupe | blob digest already exists | committed | outbox `upload.committed` |
| parts_uploading | complete_parts | all parts acknowledged | pending_commit | none |
| pending_commit | commit_upload | digest + length verified | committed | insert blob row, outbox event |
| pending_commit | commit_upload | digest mismatch or length mismatch | aborted | audit failure |
| initiated/parts_uploading/pending_commit | abort_upload | client abort or expiration | aborted | tombstone staging objects |

### Invariants

- `committed` can only be reached with verified digest and length.
- A committed upload session is terminal.
- `aborted` sessions cannot be resumed; caller creates a new session.

## 2. Publish State Machine

### States

- `draft`
- `published`
- `tombstoned`

### Transition Table

| Current State | Command | Guard | Next State | Side Effects |
|---|---|---|---|---|
| draft | publish_version | all blobs exist, policy allows | published | artifact entries persisted + outbox + audit |
| draft | publish_version | policy quarantines | draft | audit + event for quarantine |
| published | tombstone_version | authorized and retention set | tombstoned | tombstone row + outbox + audit |
| tombstoned | restore_version (optional) | within retention and policy allows | published | audit + outbox restore event |

### Invariants

- `published` version content is immutable.
- publication transaction is atomic with artifact entry persistence.
- tombstoned version remains auditable and may remain physically present until GC.

## 3. Outbox Delivery State Machine

### States

- `pending`
- `delivering`
- `delivered`
- `retry_wait`
- `dead_letter`

### Transition Table

| Current State | Event | Guard | Next State | Side Effects |
|---|---|---|---|---|
| pending | worker_claim | row lock obtained | delivering | increment attempts |
| delivering | handler_success | idempotent handler succeeded | delivered | set delivered_at |
| delivering | handler_failure | attempts < max | retry_wait | schedule next available_at |
| retry_wait | time_reached | now >= available_at | pending | none |
| delivering | handler_failure | attempts >= max | dead_letter | alert + operator queue |

### Invariants

- Request path does not depend on outbox delivery completion.
- Consumers are idempotent and safe to replay.

## 4. Delete and GC State Machine

### States

- `live`
- `tombstoned`
- `eligible_for_gc`
- `deleted`

### Transition Table

| Current State | Process | Guard | Next State | Side Effects |
|---|---|---|---|---|
| live | delete_request | authorized | tombstoned | retention deadline recorded |
| tombstoned | retention_elapsed | no active references | eligible_for_gc | mark candidate |
| eligible_for_gc | gc_sweep | not marked reachable in current run | deleted | delete object-store blob |
| eligible_for_gc | gc_mark | reachable from retained version | live | clear candidate |

### Invariants

- No hard delete without a completed mark phase in the same GC run.
- Physical deletion is always asynchronous and reversible up to retention horizon.

## 5. Failure Classes and Retry Policy

- Validation failures:
  - non-retriable, return deterministic 4xx error.
- Storage transient failures:
  - retriable with bounded exponential backoff.
- Outbox handler failures:
  - retry with jitter, then dead-letter.
- Policy engine unavailable:
  - fail closed for publish/promotion paths.
