# Release Provenance and Verification

Last updated: 2026-04-01

## Purpose

Describe how Artifortress release artifacts are produced and how operators verify them before promotion.

## Release Artifacts

The release workflow publishes:

- `artifortress-api-linux-x64.tar.gz`
- `artifortress-worker-linux-x64.tar.gz`
- `SHA256SUMS`
- CycloneDX SBOMs for API and worker
- cosign signatures and certificates for bundles and SBOMs

Source workflow:

- `.github/workflows/release-provenance.yml`

Local verification helper:

- `scripts/verify-release-artifacts.sh`

## Release Procedure

1. create signed git tag:

```bash
git tag v<version>
git push origin v<version>
```

2. wait for `release-provenance` workflow to complete
3. download release assets from GitHub Release
4. verify SHA256 sums
5. verify cosign signatures and certificates
6. inspect SBOMs before promotion into regulated environments

## Verification Example

```bash
sha256sum --check SHA256SUMS

./scripts/verify-release-artifacts.sh \
  artifortress-api-linux-x64.tar.gz \
  artifortress-api-linux-x64.tar.gz.sig \
  artifortress-api-linux-x64.tar.gz.crt
```

## Promotion Gate

Do not promote a release candidate unless all are true:

1. GitHub release workflow succeeded
2. bundle and SBOM signatures verify
3. checksums match downloaded assets
4. CI run on target commit is green
5. upgrade plan and rollback owner are assigned

## Known Follow-up

- upgrade workflow actions to Node 24-compatible upstream releases before GitHub removes Node 20 from hosted runners
