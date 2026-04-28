# Versioning And Support Policy

Last updated: 2026-04-28

## Purpose

This policy defines versioning, compatibility, deprecation, and support-window
expectations for Artifortress enterprise releases.

## Version Format

Artifortress uses semantic versioning for release tags:

```text
vMAJOR.MINOR.PATCH
```

Version meaning:
- `MAJOR`: breaking API, schema, deployment, or support-envelope change
- `MINOR`: backward-compatible feature or operational capability
- `PATCH`: backward-compatible bug fix, security fix, or documentation/evidence
  update

Release candidate tags use:

```text
vMAJOR.MINOR.PATCH-rc.N
```

## Supported Release Lines

Enterprise GA support covers:
- the current `MAJOR.MINOR` release line
- the immediately previous `MAJOR.MINOR` release line for migration and critical
  patch purposes

Patch support:
- patch releases are supported only on the latest patch for a supported
  `MAJOR.MINOR` line
- customers should move to the latest patch before opening non-critical defects
  unless the issue is explicitly tied to the upgrade path itself

## Support Windows

Default support windows:
- current minor line: full support until the next two minor releases are
  available
- previous minor line: critical security and migration support only
- release candidates: evaluation support only; not production supported unless
  explicitly approved in the release sign-off board

End of support:
- announced in release notes before the next minor release whenever possible
- requires a documented upgrade path from the retiring line to a supported line

## API Compatibility Policy

Within a major version:
- existing documented API endpoints should remain compatible
- response fields may be added
- request fields may be added only when optional or defaulted
- error responses should remain deterministic and documented
- removed endpoints, required request-field changes, and incompatible state
  transitions require a major version or an explicitly documented migration
  window

## Schema Compatibility Policy

Schema compatibility follows `docs/41-migration-compatibility-policy.md`.

Default expectations:
- prefer Class A expand-only migrations
- split Class B changes into expand, code rollout, data/backfill, and cleanup
- Class C changes require maintenance-window notes, rollback owner, and restore
  evidence

Rollback:
- application-only rollback is supported only while schema remains backward
  compatible
- restore-based rollback is the supported path for incompatible schema changes
- reverse-SQL downgrade is not supported unless rehearsed and documented for the
  specific release

## Deployment Compatibility Policy

Within a supported minor line:
- Helm values should remain backward compatible where possible
- environment variables may be added with safe defaults
- changed secret names, required values, ingress assumptions, or storage
  assumptions require explicit release-note callouts

Production rollout must preserve:
- readiness-gated traffic
- clear migration order
- backup and rollback owner
- smoke test and ops-summary verification

## Dependency Support Policy

Supported runtime baseline:
- .NET SDK `10.0.102` or newer patch release through `rollForward=latestPatch`
- .NET runtime `10.0` patch line
- PostgreSQL and S3-compatible storage capable of passing the enterprise
  verification and drill suite

Dependency changes:
- patch-level dependency updates may ship in patch releases
- dependency major-version changes require release notes and upgrade guidance
- security-driven dependency updates may be backported to supported lines

## Security Patch Policy

Security fixes may ship as patch releases.

Severity handling:
- critical: patch or documented mitigation target is `7` calendar days
- high: patch or mitigation target is `30` calendar days
- medium: patch or mitigation target is `90` calendar days
- low: handled in the next practical release unless actively exploited

Security patch releases must include:
- affected versions
- fixed version
- upgrade or mitigation instructions
- verification steps
- release provenance evidence

External vulnerability intake, severity classification, advisory publication,
and emergency patch workflow are defined in:

- `SECURITY.md`
- `docs/75-vulnerability-disclosure-and-patch-sla.md`

## Deprecation Policy

Deprecations must include:
- affected API/config/workflow
- replacement path
- first deprecated version
- earliest removal version
- migration guidance

Default notice:
- public API/config deprecations require at least one minor release of notice
- operational workflow deprecations require release-note callouts and runbook
  updates
- emergency security removals may shorten notice with explicit rationale

## Release Notes Requirements

Every release candidate and GA release must include:
- version and commit
- release type: major, minor, patch, or release candidate
- compatibility notes
- migration class summary
- upgrade and rollback notes
- security fixes and dependency changes
- known issues and unsupported claims
- links to release provenance and enterprise verification evidence

## Policy Exceptions

Any exception to this policy must be recorded in the release candidate sign-off
board with:
- owner
- rationale
- customer impact
- mitigation
- expiration or follow-up ticket
