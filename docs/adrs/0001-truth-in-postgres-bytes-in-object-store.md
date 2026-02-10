# ADR 0001: Truth in Postgres, Bytes in Object Store

- Status: accepted
- Date: 2026-02-10
- Decision Makers: Artifortress founding team

## Context

Artifact repositories fail in hard-to-repair ways when metadata and blob lifecycle are coupled in opaque storage layers. The project goals prioritize integrity, operational simplicity, and rebuildability of secondary systems.

## Decision

Artifortress will:

- Treat PostgreSQL as the single source of truth for metadata, permissions, policies, audit, and events.
- Store artifact bytes in immutable object-store blobs keyed by sha256 digest.
- Use transactional outbox for all asynchronous side effects.
- Keep search/cache/index systems eventually consistent and rebuildable from truth.

## Consequences

- Positive:
  - clear correctness boundary and easier recovery model.
  - portable object storage strategy across cloud providers.
  - simpler disaster operations for non-core subsystems.
- Negative:
  - requires strict transaction discipline on metadata writes.
  - eventual consistency in search/notifications must be accepted.
  - GC design must be carefully implemented to avoid data loss.
- Follow-up actions:
  - define publish transaction contract in code.
  - implement outbox worker idempotency policy.
  - document GC mark-and-sweep safety checks.

## Alternatives Considered

1. Unified blob+metadata in object store manifests:
   - rejected due to weak transactional semantics and difficult relational querying.
2. Embedded search-first architecture:
   - rejected due to consistency and rebuild complexity.
