# Release Candidate Sign-Off

Last updated: 2026-04-28

## Purpose

This document defines the release candidate checklist and GA sign-off board for
Kublai enterprise releases.

No release may be labeled enterprise GA unless every required gate is complete
or an exception is recorded with owner, rationale, mitigation, and expiration.

## Release Candidate Metadata

Each release candidate must record:

- release candidate tag
- target GA version
- git commit
- release owner
- release date
- migration class summary
- supported source baselines
- rollback owner
- support-envelope changes

## Required Evidence Checklist

| Gate | Required Evidence | Status |
|---|---|---|
| Source control | signed release candidate tag | required |
| CI | green CI on target commit | required |
| Unit and integration tests | included in enterprise verification report | required |
| Enterprise verification | `docs/reports/enterprise-verification-latest.md` | required |
| Throughput baseline | `docs/reports/phase2-load-baseline-latest.md` | required |
| Backup/restore drill | `docs/reports/phase6-rto-rpo-drill-latest.md` | required |
| Upgrade compatibility | `docs/reports/upgrade-compatibility-drill-latest.md` | required |
| Reliability drill | `docs/reports/reliability-drill-latest.md` | required |
| HA Kubernetes validation | `docs/reports/ha-kubernetes-validation-latest.md` | required |
| Helm certification | `docs/reports/helm-certification-latest.md` | required |
| Search soak | `docs/reports/search-soak-drill-latest.md` | required |
| Performance workflow baseline | `docs/reports/performance-workflow-baseline-latest.md` | required |
| Performance soak | `docs/reports/performance-soak-latest.md` | required |
| Release provenance | signed bundles, checksums, SBOMs, verification output | required |
| Migration review | migration class and rollback path documented | required |
| Security review | no open P0/P1 security blockers | required |
| Vulnerability handling | `SECURITY.md` and patch SLA policy reviewed | required |
| Admin CLI | `docs/76-admin-cli-operator-workflows.md` and smoke coverage reviewed | required |
| Tenant lifecycle | onboarding/offboarding workflow and lifecycle audit markers reviewed | required |
| Product envelope | `docs/59-enterprise-product-envelope.md` reviewed | required |
| Support policy | `docs/60-versioning-support-policy.md` reviewed | required |
| Documentation | release notes and operator-impact notes complete | required |

## Conditional Gates

These gates are required when the release claims the related support:

| Claim | Required Evidence |
|---|---|
| Signed container distribution | image digest, signature, SBOM, verification output |
| Signed Helm chart distribution | chart package, signature, provenance, verification output |
| Helm production install support | install/upgrade/uninstall certification result |
| Production-like capacity claim | capacity certification report |
| Air-gapped install support | offline mirror and verification evidence |
| Cloud-specific support claim | cloud reference deployment evidence |
| New package protocol support | protocol compatibility and migration evidence |

## Sign-Off Board

| Role | Required Review | Result | Owner | Date | Notes |
|---|---|---|---|---|---|
| Release owner | release scope, tag, and artifact completeness | pending | TBD | TBD | |
| Engineering | CI, tests, migrations, upgrade, rollback | pending | TBD | TBD | |
| Security | provenance, threat model, no P0/P1 blockers | pending | TBD | TBD | |
| Operations | readiness, drills, dashboard/alert impact | pending | TBD | TBD | |
| Documentation | release notes, support envelope, runbooks | pending | TBD | TBD | |
| Product/Support | support policy, known issues, customer impact | pending | TBD | TBD | |

Allowed results:
- `approved`
- `approved-with-exception`
- `rejected`
- `pending`

## Exception Record

Every exception must use this format:

```text
Exception:
- gate:
- owner:
- reason:
- customer impact:
- mitigation:
- expiration:
- follow-up ticket:
```

Exceptions may not hide a P0 launch blocker. If a P0 gate is waived, the release
candidate cannot be called enterprise GA until the exception is resolved or the
support envelope is narrowed to remove the claim.

## GA Promotion Rules

An RC may be promoted to enterprise GA only when:

1. all required gates are `approved`
2. all conditional gates for claimed support are `approved`
3. release provenance verifies from downloaded artifacts
4. upgrade and rollback paths match the published compatibility policy
5. the product envelope contains no unsupported implied claims
6. sign-off board has no unresolved `rejected` or `pending` rows

## Rejection Rules

Reject the release candidate if any of these are true:

- enterprise verification fails
- release provenance cannot be verified
- upgrade path from a supported baseline fails
- backup/restore evidence is missing
- migration class is unclear
- rollback owner is missing
- open P0/P1 security blocker exists
- release notes omit a known breaking change or unsupported claim

## Release Archive

For each RC or GA release, archive:

- this sign-off board with completed owner/date/result values
- release notes
- enterprise verification report
- drill and baseline reports
- release provenance verification output
- exception records

Archive evidence should reference the release tag and commit.
