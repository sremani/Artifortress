# Release Artifact Drill Certification

Last updated: 2026-04-28

## Purpose

This document defines how `EGA-23` closes: backup/restore and upgrade drills
must be traceable to the release artifacts promoted for the target environment,
not only to a local source tree.

The supporting scripts now record release metadata in both required reports.
The ticket is complete only after those reports are generated from the same
verified release artifact set.

## Required Artifact Inputs

Set these environment variables before running certification drills:

| Variable | Meaning |
| --- | --- |
| `KUBLAI_RELEASE_TAG` | signed release tag under test |
| `KUBLAI_API_IMAGE_DIGEST` | digest-pinned API image reference or digest |
| `KUBLAI_WORKER_IMAGE_DIGEST` | digest-pinned worker image reference or digest |
| `KUBLAI_HELM_CHART_DIGEST` | SHA-256 digest of the packaged Helm chart |
| `KUBLAI_RELEASE_SBOM_PATH` | local path or release asset path for the SBOM bundle |
| `KUBLAI_RELEASE_PROVENANCE_REPORT` | provenance evidence report for the same tag |

The values should come from the downloaded GitHub Release assets verified by
`make release-provenance-certify TAG=v<version>`.

## Certification Flow

1. Certify release provenance:

```bash
make release-provenance-certify TAG=v<version>
```

2. Export the release artifact metadata from the verified release bundle:

```bash
export KUBLAI_RELEASE_TAG=v<version>
export KUBLAI_API_IMAGE_DIGEST=ghcr.io/<owner>/kublai-api@sha256:<digest>
export KUBLAI_WORKER_IMAGE_DIGEST=ghcr.io/<owner>/kublai-worker@sha256:<digest>
export KUBLAI_HELM_CHART_DIGEST=sha256:<chart-digest>
export KUBLAI_RELEASE_SBOM_PATH=dist/kublai-helm-chart.sbom.cdx.json
export KUBLAI_RELEASE_PROVENANCE_REPORT=docs/reports/release-provenance-latest.md
```

3. Install or upgrade the target environment from those release artifacts.

4. Run the backup/restore drill:

```bash
make phase6-drill
```

5. Run the upgrade compatibility drill:

```bash
make upgrade-compatibility-drill
```

6. Validate that both reports contain release artifact metadata:

```bash
make release-artifact-drill-validate
```

## Required Evidence

The release sign-off packet must contain:

- `docs/reports/release-provenance-latest.md`
- `docs/reports/phase6-rto-rpo-drill-latest.md`
- `docs/reports/upgrade-compatibility-drill-latest.md`
- artifact digests recorded in both drill reports
- release tag consistency across provenance, backup/restore, and upgrade
  evidence

## Unsupported Shortcuts

Do not use source-tree-only builds as EGA-23 certification evidence. Local
source-tree drill runs remain useful regression checks, but production
certification requires release tag, image digest, chart digest, SBOM path, and
provenance report references in the drill evidence.
