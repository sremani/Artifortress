# Release Provenance and Verification

Last updated: 2026-04-28

## Purpose

Describe how Artifortress release artifacts are produced and how operators verify them before promotion.

## Release Artifacts

The release workflow publishes:

- `artifortress-api-linux-x64.tar.gz`
- `artifortress-worker-linux-x64.tar.gz`
- `ghcr.io/<owner>/artifortress-api:<version>`
- `ghcr.io/<owner>/artifortress-worker:<version>`
- immutable commit tags for API and worker images: `sha-<short-commit>`
- digest-pinned image references in `OCI-IMAGES.txt`
- packaged Helm chart: `artifortress-<version>.tgz`
- `SHA256SUMS`
- CycloneDX SBOMs for API and worker bundle contents
- CycloneDX SBOMs for API and worker OCI images
- CycloneDX SBOM for the packaged Helm chart
- cosign signatures and certificates for bundles, SBOMs, and chart package
- cosign signatures for API and worker image digests

Source workflow:

- `.github/workflows/release-provenance.yml`

Local verification helper:

- `scripts/verify-release-artifacts.sh`

Release provenance certification helper:

- `scripts/release-provenance-certify.sh`
- `make release-provenance-certify TAG=v<version>`
- report output: `docs/reports/release-provenance-latest.md`

## Release Procedure

1. create signed git tag from a clean release-candidate commit:

```bash
git tag -s v<version> -m "Artifortress v<version>"
git push origin v<version>
```

2. wait for `release-provenance` workflow to complete
3. certify release assets and write durable evidence:

```bash
make release-provenance-certify TAG=v<version>
```

4. inspect `docs/reports/release-provenance-latest.md`
5. inspect SBOMs before promotion into regulated environments
6. pin deployment values to the verified image digests or immutable release tags

## Verification Example

The certification helper performs the following checks against downloaded
GitHub Release assets.

```bash
./scripts/verify-release-artifacts.sh checksum SHA256SUMS

./scripts/verify-release-artifacts.sh \
  artifortress-api-linux-x64.tar.gz \
  artifortress-api-linux-x64.tar.gz.sig \
  artifortress-api-linux-x64.tar.gz.crt

./scripts/verify-release-artifacts.sh blob \
  artifortress-<version>.tgz \
  artifortress-<version>.tgz.sig \
  artifortress-<version>.tgz.crt
```

Image verification:

```bash
api_ref="$(sed -n 's/^api image: //p' OCI-IMAGES.txt)"
worker_ref="$(sed -n 's/^worker image: //p' OCI-IMAGES.txt)"

./scripts/verify-release-artifacts.sh image "$api_ref"
./scripts/verify-release-artifacts.sh image "$worker_ref"
```

SBOM verification examples:

```bash
./scripts/verify-release-artifacts.sh blob \
  artifortress-api-image.sbom.cdx.json \
  artifortress-api-image.sbom.cdx.json.sig \
  artifortress-api-image.sbom.cdx.json.crt

./scripts/verify-release-artifacts.sh blob \
  artifortress-helm-chart.sbom.cdx.json \
  artifortress-helm-chart.sbom.cdx.json.sig \
  artifortress-helm-chart.sbom.cdx.json.crt
```

## Promotion Gate

Do not promote a release candidate unless all are true:

1. GitHub release workflow succeeded
2. bundle, SBOM, chart, and image signatures verify
3. checksums match downloaded assets
4. API and worker images are referenced by version tag and digest
5. Helm chart package version matches the app release version
6. CI run on target commit is green
7. upgrade plan and rollback owner are assigned
8. `docs/reports/release-provenance-latest.md` reports `PASS`

## Known Follow-up

- none for release provenance at this time

## Latest Evidence

- signed tag: `v0.1.0-rc.2`
- release workflow run: `25079528537`
- GitHub Release type: prerelease
- evidence report: `docs/reports/release-provenance-latest.md`
- status: `PASS`
