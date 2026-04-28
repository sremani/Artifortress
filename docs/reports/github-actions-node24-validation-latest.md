# GitHub Actions Node 24 Validation Report

Generated at: 2026-04-28T21:53:28Z

## Summary

- overall status: PASS
- ticket: `EGA-14`
- purpose: remove GitHub Actions Node 20 runtime deprecation risk

## Action Upgrades

| Workflow | Action | Previous | Updated |
|---|---|---:|---:|
| `ci.yml` | `actions/checkout` | `v5` | `v6` |
| `helm-certification.yml` | `actions/checkout` | `v5` | `v6` |
| `mutation-track.yml` | `actions/checkout` | `v4` | `v6` |
| `mutation-track.yml` | `actions/setup-dotnet` | `v4` | `v5` |
| `mutation-track.yml` | `actions/cache/restore` | `v4` | `v5` |
| `mutation-track.yml` | `actions/cache/save` | `v4` | `v5` |
| `mutation-track.yml` | `actions/upload-artifact` | `v4` | `v7` |
| `release-provenance.yml` | `actions/checkout` | `v4` | `v6` |
| `release-provenance.yml` | `actions/setup-dotnet` | `v4` | `v5` |
| `release-provenance.yml` | `softprops/action-gh-release` | `v2` | `v3` |

## Upstream Versions Checked

- `actions/checkout`: `v6.0.2`
- `actions/setup-dotnet`: `v5.2.0`
- `actions/upload-artifact`: `v7.0.1`
- `actions/cache`: `v5.0.5`
- `softprops/action-gh-release`: `v3.0.0`

## Local Validation

- workflow YAML parses successfully
- workflow inventory no longer uses the old checkout major
- workflow inventory no longer uses the old setup-dotnet major
- workflow inventory no longer uses the old upload-artifact major
- workflow inventory no longer uses the old cache restore/save major
- workflow inventory no longer uses the old action-gh-release major

## Remote Validation

| Workflow | Run | Trigger | Status | Notes |
|---|---:|---|---|---|
| `ci.yml` | `25079146581` | push to `master` | PASS | build, test, format, and integration tests passed |
| `helm-certification.yml` | `25079049857` | push to `master` | PASS | Helm certification passed on upgraded checkout action |
| `mutation-track.yml` | `25079314883` | `workflow_dispatch` | PASS | workflow passed; Track B remains continue-on-error, native lane passed |
| `release-provenance.yml` | `25079528537` | signed tag `v0.1.0-rc.2` | PASS | release workflow passed after action upgrades |

## Release Validation

- signed validation tag: `v0.1.0-rc.2`
- release: `https://github.com/sremani/Artifortress/releases/tag/v0.1.0-rc.2`
- release provenance evidence: `docs/reports/release-provenance-latest.md`
- release asset certification: PASS

The prior real release workflow run `25078042079` succeeded but emitted Node 20
warnings before these action upgrades. The validation release run `25079528537`
completed after the upgrades without a Node 20 deprecation annotation.
