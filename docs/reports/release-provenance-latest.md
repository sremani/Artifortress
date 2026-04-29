# Release Provenance Evidence

Generated at: 2026-04-28T21:53:28Z

## Summary

- overall status: PASS
- tag: v0.1.0-rc.2
- version: 0.1.0-rc.2
- tag subject: Kublai v0.1.0-rc.2
- tag commit: 6d9f8e8e9dcb489eaff03a02583b25025e348e94
- asset directory: `/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2`
- started at: 2026-04-28T21:53:21Z
- ended at: 2026-04-28T21:53:28Z
- total duration seconds: 7

## Verified OCI Images

- API: `ghcr.io/sremani/kublai-api@sha256:6f732dbc6341b311bf12557c31dc8f84fa7ba8b9db74d83da2c169fbb062b021`
- Worker: `ghcr.io/sremani/kublai-worker@sha256:ff4f6f47d6b5c70311c928e19950588579b63ffa8b6969c41f193c46d130e6a8`

## Verification Steps

| Step | Command | Status | Duration Seconds | Log |
|---|---|---:|---:|---|
| fetch release tag | `cd '/home/srikanth/projects/Kublai' && git fetch origin 'refs/tags/v0.1.0-rc.2:refs/tags/v0.1.0-rc.2'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/fetch-release-tag.log` |
| verify signed git tag | `cd '/home/srikanth/projects/Kublai' && git verify-tag 'v0.1.0-rc.2'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-signed-git-tag.log` |
| check remote tag | `cd '/home/srikanth/projects/Kublai' && git ls-remote --exit-code --tags origin 'refs/tags/v0.1.0-rc.2'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/check-remote-tag.log` |
| check GitHub release | `cd '/home/srikanth/projects/Kublai' && gh release view 'v0.1.0-rc.2'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/check-GitHub-release.log` |
| download release assets | `cd '/home/srikanth/projects/Kublai' && gh release download 'v0.1.0-rc.2' --dir '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' --clobber` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/download-release-assets.log` |
| verify checksums | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' checksum SHA256SUMS` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-checksums.log` |
| verify kublai-api-linux-x64.tar.gz signature | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' blob 'kublai-api-linux-x64.tar.gz' 'kublai-api-linux-x64.tar.gz.sig' 'kublai-api-linux-x64.tar.gz.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-kublai-api-linux-x64-tar-gz-signature.log` |
| verify kublai-worker-linux-x64.tar.gz signature | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' blob 'kublai-worker-linux-x64.tar.gz' 'kublai-worker-linux-x64.tar.gz.sig' 'kublai-worker-linux-x64.tar.gz.crt'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/verify-kublai-worker-linux-x64-tar-gz-signature.log` |
| verify kublai-api.sbom.cdx.json signature | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' blob 'kublai-api.sbom.cdx.json' 'kublai-api.sbom.cdx.json.sig' 'kublai-api.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-kublai-api-sbom-cdx-json-signature.log` |
| verify kublai-worker.sbom.cdx.json signature | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' blob 'kublai-worker.sbom.cdx.json' 'kublai-worker.sbom.cdx.json.sig' 'kublai-worker.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-kublai-worker-sbom-cdx-json-signature.log` |
| verify kublai-api-image.sbom.cdx.json signature | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' blob 'kublai-api-image.sbom.cdx.json' 'kublai-api-image.sbom.cdx.json.sig' 'kublai-api-image.sbom.cdx.json.crt'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/verify-kublai-api-image-sbom-cdx-json-signature.log` |
| verify kublai-worker-image.sbom.cdx.json signature | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' blob 'kublai-worker-image.sbom.cdx.json' 'kublai-worker-image.sbom.cdx.json.sig' 'kublai-worker-image.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-kublai-worker-image-sbom-cdx-json-signature.log` |
| verify kublai-helm-chart.sbom.cdx.json signature | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' blob 'kublai-helm-chart.sbom.cdx.json' 'kublai-helm-chart.sbom.cdx.json.sig' 'kublai-helm-chart.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-kublai-helm-chart-sbom-cdx-json-signature.log` |
| verify kublai-0.1.0-rc.2.tgz signature | `cd '/home/srikanth/projects/Kublai/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' blob 'kublai-0.1.0-rc.2.tgz' 'kublai-0.1.0-rc.2.tgz.sig' 'kublai-0.1.0-rc.2.tgz.crt'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/verify-kublai-0-1-0-rc-2-tgz-signature.log` |
| verify API image signature | `cd '/home/srikanth/projects/Kublai' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' image 'ghcr.io/sremani/kublai-api@sha256:6f732dbc6341b311bf12557c31dc8f84fa7ba8b9db74d83da2c169fbb062b021'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-API-image-signature.log` |
| verify worker image signature | `cd '/home/srikanth/projects/Kublai' && '/home/srikanth/projects/Kublai/scripts/verify-release-artifacts.sh' image 'ghcr.io/sremani/kublai-worker@sha256:ff4f6f47d6b5c70311c928e19950588579b63ffa8b6969c41f193c46d130e6a8'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/verify-worker-image-signature.log` |

## Required Release Assets

- `kublai-api-linux-x64.tar.gz`
- `kublai-api-linux-x64.tar.gz.sig`
- `kublai-api-linux-x64.tar.gz.crt`
- `kublai-worker-linux-x64.tar.gz`
- `kublai-worker-linux-x64.tar.gz.sig`
- `kublai-worker-linux-x64.tar.gz.crt`
- `kublai-api.sbom.cdx.json`
- `kublai-api.sbom.cdx.json.sig`
- `kublai-api.sbom.cdx.json.crt`
- `kublai-worker.sbom.cdx.json`
- `kublai-worker.sbom.cdx.json.sig`
- `kublai-worker.sbom.cdx.json.crt`
- `kublai-api-image.sbom.cdx.json`
- `kublai-api-image.sbom.cdx.json.sig`
- `kublai-api-image.sbom.cdx.json.crt`
- `kublai-worker-image.sbom.cdx.json`
- `kublai-worker-image.sbom.cdx.json.sig`
- `kublai-worker-image.sbom.cdx.json.crt`
- `kublai-helm-chart.sbom.cdx.json`
- `kublai-helm-chart.sbom.cdx.json.sig`
- `kublai-helm-chart.sbom.cdx.json.crt`
- `kublai-0.1.0-rc.2.tgz`
- `kublai-0.1.0-rc.2.tgz.sig`
- `kublai-0.1.0-rc.2.tgz.crt`
- `OCI-IMAGES.txt`
- `SHA256SUMS`
