# Object Storage Independence And MinIO Exit Plan

Last updated: 2026-04-28

## Purpose

This document tracks the Artifortress plan to remove MinIO as a strategic
dependency after the upstream open-source repository was archived and made
read-only.

Artifortress should continue to support S3-compatible object storage as an
interface, but the project should not depend on unmaintained MinIO community
artifacts as the default validation or self-hosted production path.

## Current Risk

The repository currently uses MinIO in local and kind validation paths:

- Docker Compose development stack
- kind Kubernetes dependency stack
- Helm certification
- HA Kubernetes validation
- local storage bootstrap scripts

Production guidance already permits managed S3-compatible object storage such
as cloud object storage. That remains the preferred near-term path for
`artifortress.com`.

Risk posture:

- using managed S3-compatible storage for production is acceptable
- using MinIO as a local test fixture is acceptable only as a temporary bridge
- presenting MinIO as the recommended self-hosted object-store dependency is no
  longer acceptable for enterprise GA

## Near-Term Deployment Decision

For `artifortress.com`, do not self-host object storage.

Use a managed S3-compatible provider:

- DigitalOcean Spaces for a DigitalOcean launch
- AWS S3 if the production region/provider moves to AWS
- Cloudflare R2 if egress cost and Cloudflare integration dominate

The Artifortress deployment must validate against the selected managed object
store before production cutover.

## Side Project Proposal

Create a separate side project for an Artifortress-owned object store:

- working name: `FortressStore`
- purpose: small, boring, S3-compatible object storage for Artifortress
  deployments and tests
- license: choose deliberately before coding
- implementation language: TBD
- first target: single-node durable object storage with the exact S3 subset
  Artifortress needs

This should be a separate repository, not a subdirectory in Artifortress, so the
object store can evolve with its own release cadence, security policy, and
compatibility test suite.

## Minimum S3 Compatibility Surface

Artifortress currently needs:

- bucket existence and creation for local/dev paths
- object put/get/delete
- multipart upload create, upload part, complete, and abort
- range reads
- object metadata needed for digest/length validation
- deterministic error behavior for missing objects and failed uploads

Nice-to-have later:

- bucket lifecycle policies
- object versioning
- server-side encryption hooks
- retention/legal-hold integration
- replication
- admin API and web console

Do not start with a full S3 clone. Start with the subset Artifortress actually
uses and prove it with contract tests.

## Migration Plan

Phase 1: dependency boundary

- Add an object-storage compatibility matrix.
- Keep MinIO only as a temporary test fixture.
- Add a managed-object-store certification path for `artifortress.com`.
- Ensure Artifortress docs say "S3-compatible object storage" rather than
  recommending MinIO.

Phase 2: replacement evaluation

- Compare managed providers: DigitalOcean Spaces, AWS S3, Cloudflare R2.
- Compare self-hosted alternatives and maintained forks.
- Decide whether `FortressStore` is worth building before first production
  customer or should remain a follow-up.

Phase 3: side-project bootstrap

- Create the `FortressStore` repository.
- Define the S3 subset contract.
- Build an Artifortress object-storage conformance test suite.
- Implement single-node local mode.
- Add Docker image and Helm/Compose examples.

Phase 4: production hardening

- Add durability model, fsync/write-ahead semantics, and corruption detection.
- Add backup/restore workflow.
- Add metrics and health checks.
- Add security review and release provenance.

## Artifortress Changes Needed

- Remove MinIO language from customer-facing production recommendations.
- Keep `deploy/kind/dependencies.yaml` explicitly marked as temporary local
  validation infrastructure.
- Add provider-specific object-storage examples for the selected production
  provider.
- Add contract tests that can run against MinIO, managed S3-compatible storage,
  and future `FortressStore`.
- Add a release gate requiring object-storage provider certification for
  `artifortress.com`.

## Acceptance Criteria

- A ticket exists on the enterprise GA board.
- Production hosting plan stops implying MinIO for production.
- Local validation docs identify MinIO as temporary.
- A side-project decision record exists for `FortressStore`.
- Artifortress has an object-storage compatibility test plan.
- `artifortress.com` production cutover uses managed object storage until a
  supported replacement is validated.

## References

- Upstream repository state: `https://github.com/minio/minio/issues/21714`
- Artifortress hosting plan:
  `docs/73-artifortress-com-production-hosting-plan.md`
- Artifortress production cutover:
  `docs/72-installation-and-production-cutover-guide.md`
