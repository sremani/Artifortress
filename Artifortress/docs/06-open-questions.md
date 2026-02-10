# Open Design Questions

These questions should be resolved before deep implementation work.

## 1. Consistency and Replication

- What is the required read-after-publish guarantee across regions?
- Is failover read-only or read-write during primary DB outage?
- Do we need per-repo replication lag visibility in API responses?

## 2. Ecosystem Semantics

- How should mutable tags/channels (for example Docker tags, npm dist-tags) be modeled against immutable version entities?
- Which package ecosystems are in v1 scope and what validators are mandatory for each?
- How should remote repository metadata caching and staleness be exposed to clients?

## 3. Security and Compliance

- What token revocation SLA is acceptable (for example < 60 seconds)?
- Which audit events are compliance-critical and what retention period is mandatory?
- Is KMS-backed per-tenant keying required in v1?

## 4. Policy and Quarantine

- Should publish always fail closed on scanner/policy timeout, or allow explicit break-glass override?
- Which policy outcomes block download vs only block promotion?
- How are false positives in quarantine triaged and approved?

## 5. Operations

- What is the target RPO/RTO for metadata and blobs?
- Which reconcile checks run continuously versus on-demand?
- What is the expected maximum object count per tenant for capacity planning?

## 6. Product Boundary

- Is replication from other artifact managers a migration-only feature or ongoing federation capability?
- Are UI workflows required for v1 launch, or can CLI/API-only be launch-ready?
- Do we need billing/usage metering in core architecture now, or as a later service?
