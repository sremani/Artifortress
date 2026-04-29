# Air-Gapped Offline Install Plan

Last updated: 2026-04-28

## Purpose

This document closes the planning side of `EGA-22`: how to install and upgrade
Kublai in a restricted environment without pulling from public registries
during deployment.

The plan assumes a connected staging side that downloads and verifies release
artifacts, and an offline enclave side that receives a complete mirrored bundle.

## Offline Bundle Contents

Each release bundle transferred into the restricted environment must contain:

| Artifact | Purpose |
| --- | --- |
| `kublai-<version>.tgz` | Helm chart package |
| `kublai-<version>.tgz.sig` and `.crt` | chart signature and signing certificate |
| `kublai-api-linux-x64.tar.gz` | API binary bundle |
| `kublai-worker-linux-x64.tar.gz` | worker binary bundle |
| bundle signatures and certificates | blob verification material |
| `OCI-IMAGES.txt` | digest-pinned API and worker image references |
| API and worker OCI images | mirrored into the offline registry |
| API, worker, image, and chart SBOMs | release composition evidence |
| SBOM signatures and certificates | SBOM verification material |
| `SHA256SUMS` | checksum manifest |
| release provenance report | connected-side evidence for the same tag |
| `deploy/offline/release-manifest.example.env` derivative | operator manifest for the transferred bundle |

Do not transfer mutable tags as the only image reference. Mirror digest-pinned
images and record the digest values in the release manifest.

## Mirror Procedure

On a connected staging machine:

1. Certify release provenance:

```bash
make release-provenance-certify TAG=v<version>
```

2. Copy the verified release asset directory to a staging bundle directory.
3. Copy `docs/reports/release-provenance-latest.md` into the bundle.
4. Mirror the API and worker images from `OCI-IMAGES.txt` into the offline
   registry namespace.
5. Export a release manifest by copying
   `deploy/offline/release-manifest.example.env` and replacing every digest,
   file name, and registry value with the verified release values.
6. Create a transfer archive from the complete bundle.
7. Transfer the archive through the organization's approved removable-media or
   cross-domain process.

## Offline Verification

Inside the restricted environment:

1. Unpack the transferred bundle.
2. Verify `SHA256SUMS` against the unpacked files:

```bash
sha256sum --check SHA256SUMS
```

3. Verify blob signatures and certificates for the chart, binaries, and SBOMs
   using the same identity and issuer policy documented in
   `scripts/verify-release-artifacts.sh`.
4. Confirm the offline registry contains the API and worker image digests listed
   in the release manifest.
5. Keep the connected-side release provenance report with the offline install
   evidence.

If the offline enclave cannot perform keyless sigstore verification with its
approved trust-root material, the connected-side verification report becomes
the promotion evidence and that limitation must be recorded in the install
exception log. Do not claim fully offline cryptographic re-verification unless
the enclave has the required trust roots and cosign behavior validated.

## Offline Install Procedure

1. Load or mirror the API and worker images into the internal registry.
2. Create Kubernetes Secrets from the offline secret-management workflow.
3. Create a production values file derived from the cloud or production values
   examples, but point images at the internal registry and use digest-pinned
   references where the registry supports them.
4. Install from the local chart package:

```bash
helm upgrade --install kublai-prod ./kublai-<version>.tgz \
  --namespace kublai-prod \
  --create-namespace \
  --values /secure/path/kublai-prod.offline.values.yaml
```

5. Run readiness, smoke, backup/restore, and upgrade evidence gates using the
   offline environment's internal endpoints.

## Offline Upgrade Procedure

1. Transfer and verify the new release bundle.
2. Confirm the previous release bundle remains available for rollback.
3. Mirror the new images into the offline registry.
4. Capture a fresh database backup.
5. Run `helm upgrade` from the new local chart package.
6. Apply migrations according to `docs/25-upgrade-rollback-runbook.md`.
7. Run `make phase6-drill`, `make upgrade-compatibility-drill`, and
   `make release-artifact-drill-validate` with release metadata exported.
8. Keep the old chart package, old image digests, and restore backup until the
   upgrade window closes.

## Unsupported Assumptions

The current plan does not support:

- deployment that pulls from GitHub Container Registry or GitHub Releases from
  inside the restricted environment
- mutable image tags as release evidence
- offline installs without mirrored SBOMs, signatures, certificates, and
  checksums
- fully offline keyless verification unless the customer has validated cosign
  trust-root behavior in the enclave
- managed cloud dependencies created from this repository; Terraform/OpenTofu is
  tracked separately in `EGA-29`
- package-manager protocol compatibility beyond the documented Kublai API

## Validation

Validate the plan shape and manifest keys:

```bash
make offline-install-plan-validate
```

This validation proves the repository has the required plan sections and bundle
manifest fields. It does not prove a customer enclave can verify a specific
release; that proof belongs in the release-specific install evidence packet.
