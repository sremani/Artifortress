# Package Format Compatibility Strategy

Last updated: 2026-04-28

## Purpose

This document closes `EGA-25` by stating what package clients Kublai
supports at enterprise GA and what remains future protocol work.

## GA Position

Kublai enterprise GA supports the Kublai HTTP API only.

Day-one support includes:

- repository administration
- tenant and repository RBAC
- multipart upload and digest-verified blob storage
- package version drafts
- artifact entries
- manifest publication
- immutable published-version behavior
- policy, quarantine, audit, retention, lifecycle, and search workflows
- supported admin CLI workflows over the Kublai API

Day-one support does not include native package-manager protocols such as
NuGet, Maven, npm, OCI Distribution, PyPI, RubyGems, Cargo, Debian, RPM, or
generic proxy-cache behavior.

This is an intentional support boundary, not an accidental gap. The current
product is a correctness-first artifact repository control plane with explicit
APIs and audit semantics. Protocol compatibility should be added only when the
server behavior, client edge cases, and conformance tests can be supported.

## Customer-Facing Language

Use this language in procurement, sales engineering, and evaluator material:

Kublai is an enterprise artifact repository with a supported HTTP API and
admin CLI. It is not yet a drop-in replacement for NuGet, Maven, npm, OCI, or
PyPI registries. Teams can integrate CI/CD systems through the Kublai API
today. Native package-manager endpoints are tracked as future compatibility
work and require separate acceptance tests before any support claim is made.

## Future Protocol Tracks

| Track | Priority | Initial Goal | GA Claim Blocker |
| --- | --- | --- | --- |
| Generic blob repository | P1 follow-up | client-neutral upload/download conventions and manifest metadata | compatibility tests for checksum, range read, retention, and audit behavior |
| NuGet | P2 candidate | .NET package publish and restore flow | package index, semver, symbol package, delete/unlist, and client-cache conformance tests |
| OCI Distribution | P2 candidate | container/artifact push and pull | manifest list, blob mount, auth challenge, referrers, and digest conformance tests |
| npm | P3 candidate | scoped package publish and install | dist-tags, tarball integrity, auth token, metadata mutation, and client-cache conformance tests |
| Maven | P3 candidate | Maven package deploy and resolve | snapshot/release behavior, checksums, metadata XML, and proxy semantics tests |
| PyPI | P3 candidate | upload and install with pip | simple API, wheel/sdist metadata, yanked releases, and hash-checking tests |

The first compatibility implementation should be the generic blob repository
track unless a paying customer contract requires a specific package manager.
Generic blob semantics are closest to the existing Kublai API and force
the least protocol-specific behavior into the core.

## Compatibility Test Matrix

Every future protocol ticket must include tests for:

| Test Class | Required Coverage |
| --- | --- |
| Publish success | client can publish using the official client or protocol reference flow |
| Read success | client can install/pull/restore the published artifact |
| Digest integrity | server and client agree on checksum or digest semantics |
| Immutability | published versions cannot be overwritten when the protocol permits mutation |
| Delete semantics | delete, unlist, tombstone, or retention behavior is explicit |
| Authz | tenant/repository permissions map to protocol operations |
| Audit | protocol operations emit tenant-scoped audit events |
| Quarantine/policy | blocked artifacts fail closed and report deterministic diagnostics |
| Range/partial read | clients that use partial downloads behave correctly |
| Client cache edge cases | stale metadata, retries, redirects, and conditional requests are tested |
| Migration | import from incumbent repositories preserves content and evidence |

No protocol track may enter the enterprise support envelope until its test
matrix is automated and listed in release evidence.

## Follow-Up Protocol Tickets

These tickets are intentionally outside the current enterprise GA tranche:

| Ticket | Scope | Dependency |
| --- | --- | --- |
| PFC-01 | Design generic blob repository conventions | `EGA-25` |
| PFC-02 | Add generic blob repository compatibility tests | `PFC-01` |
| PFC-03 | Decide first native package-manager protocol based on customer demand | `EGA-28`, first customer discovery |
| PFC-04 | Build protocol-specific conformance harness for selected package manager | `PFC-03` |
| PFC-05 | Add migration dry-run and checksum verification for selected package manager | `PFC-03`, `EGA-28` |

## Procurement Answers

If asked whether Kublai supports a specific package manager:

- Answer yes only for the Kublai HTTP API and admin CLI.
- Answer no for drop-in package-manager registry compatibility at enterprise
  GA.
- Offer the future protocol track table as roadmap context, not as a committed
  delivery date.
- Require customer discovery before ranking NuGet, OCI, npm, Maven, or PyPI
  above the generic blob track.

## Evidence Links

- `docs/59-enterprise-product-envelope.md`
- `docs/76-admin-cli-operator-workflows.md`
- `docs/77-tenant-onboarding-and-offboarding-workflow.md`
- future `EGA-28` migration guide
