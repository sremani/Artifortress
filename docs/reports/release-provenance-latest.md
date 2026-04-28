# Release Provenance Evidence

Generated at: 2026-04-28T21:53:28Z

## Summary

- overall status: PASS
- tag: v0.1.0-rc.2
- version: 0.1.0-rc.2
- tag subject: Artifortress v0.1.0-rc.2
- tag commit: 6d9f8e8e9dcb489eaff03a02583b25025e348e94
- asset directory: `/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2`
- started at: 2026-04-28T21:53:21Z
- ended at: 2026-04-28T21:53:28Z
- total duration seconds: 7

## Verified OCI Images

- API: `ghcr.io/sremani/artifortress-api@sha256:6f732dbc6341b311bf12557c31dc8f84fa7ba8b9db74d83da2c169fbb062b021`
- Worker: `ghcr.io/sremani/artifortress-worker@sha256:ff4f6f47d6b5c70311c928e19950588579b63ffa8b6969c41f193c46d130e6a8`

## Verification Steps

| Step | Command | Status | Duration Seconds | Log |
|---|---|---:|---:|---|
| fetch release tag | `cd '/home/srikanth/projects/Artifortress' && git fetch origin 'refs/tags/v0.1.0-rc.2:refs/tags/v0.1.0-rc.2'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/fetch-release-tag.log` |
| verify signed git tag | `cd '/home/srikanth/projects/Artifortress' && git verify-tag 'v0.1.0-rc.2'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-signed-git-tag.log` |
| check remote tag | `cd '/home/srikanth/projects/Artifortress' && git ls-remote --exit-code --tags origin 'refs/tags/v0.1.0-rc.2'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/check-remote-tag.log` |
| check GitHub release | `cd '/home/srikanth/projects/Artifortress' && gh release view 'v0.1.0-rc.2'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/check-GitHub-release.log` |
| download release assets | `cd '/home/srikanth/projects/Artifortress' && gh release download 'v0.1.0-rc.2' --dir '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' --clobber` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/download-release-assets.log` |
| verify checksums | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' checksum SHA256SUMS` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-checksums.log` |
| verify artifortress-api-linux-x64.tar.gz signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-api-linux-x64.tar.gz' 'artifortress-api-linux-x64.tar.gz.sig' 'artifortress-api-linux-x64.tar.gz.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-artifortress-api-linux-x64-tar-gz-signature.log` |
| verify artifortress-worker-linux-x64.tar.gz signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-worker-linux-x64.tar.gz' 'artifortress-worker-linux-x64.tar.gz.sig' 'artifortress-worker-linux-x64.tar.gz.crt'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/verify-artifortress-worker-linux-x64-tar-gz-signature.log` |
| verify artifortress-api.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-api.sbom.cdx.json' 'artifortress-api.sbom.cdx.json.sig' 'artifortress-api.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-artifortress-api-sbom-cdx-json-signature.log` |
| verify artifortress-worker.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-worker.sbom.cdx.json' 'artifortress-worker.sbom.cdx.json.sig' 'artifortress-worker.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-artifortress-worker-sbom-cdx-json-signature.log` |
| verify artifortress-api-image.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-api-image.sbom.cdx.json' 'artifortress-api-image.sbom.cdx.json.sig' 'artifortress-api-image.sbom.cdx.json.crt'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/verify-artifortress-api-image-sbom-cdx-json-signature.log` |
| verify artifortress-worker-image.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-worker-image.sbom.cdx.json' 'artifortress-worker-image.sbom.cdx.json.sig' 'artifortress-worker-image.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-artifortress-worker-image-sbom-cdx-json-signature.log` |
| verify artifortress-helm-chart.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-helm-chart.sbom.cdx.json' 'artifortress-helm-chart.sbom.cdx.json.sig' 'artifortress-helm-chart.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-artifortress-helm-chart-sbom-cdx-json-signature.log` |
| verify artifortress-0.1.0-rc.2.tgz signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.2' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-0.1.0-rc.2.tgz' 'artifortress-0.1.0-rc.2.tgz.sig' 'artifortress-0.1.0-rc.2.tgz.crt'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/verify-artifortress-0-1-0-rc-2-tgz-signature.log` |
| verify API image signature | `cd '/home/srikanth/projects/Artifortress' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' image 'ghcr.io/sremani/artifortress-api@sha256:6f732dbc6341b311bf12557c31dc8f84fa7ba8b9db74d83da2c169fbb062b021'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.2/verify-API-image-signature.log` |
| verify worker image signature | `cd '/home/srikanth/projects/Artifortress' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' image 'ghcr.io/sremani/artifortress-worker@sha256:ff4f6f47d6b5c70311c928e19950588579b63ffa8b6969c41f193c46d130e6a8'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.2/verify-worker-image-signature.log` |

## Required Release Assets

- `artifortress-api-linux-x64.tar.gz`
- `artifortress-api-linux-x64.tar.gz.sig`
- `artifortress-api-linux-x64.tar.gz.crt`
- `artifortress-worker-linux-x64.tar.gz`
- `artifortress-worker-linux-x64.tar.gz.sig`
- `artifortress-worker-linux-x64.tar.gz.crt`
- `artifortress-api.sbom.cdx.json`
- `artifortress-api.sbom.cdx.json.sig`
- `artifortress-api.sbom.cdx.json.crt`
- `artifortress-worker.sbom.cdx.json`
- `artifortress-worker.sbom.cdx.json.sig`
- `artifortress-worker.sbom.cdx.json.crt`
- `artifortress-api-image.sbom.cdx.json`
- `artifortress-api-image.sbom.cdx.json.sig`
- `artifortress-api-image.sbom.cdx.json.crt`
- `artifortress-worker-image.sbom.cdx.json`
- `artifortress-worker-image.sbom.cdx.json.sig`
- `artifortress-worker-image.sbom.cdx.json.crt`
- `artifortress-helm-chart.sbom.cdx.json`
- `artifortress-helm-chart.sbom.cdx.json.sig`
- `artifortress-helm-chart.sbom.cdx.json.crt`
- `artifortress-0.1.0-rc.2.tgz`
- `artifortress-0.1.0-rc.2.tgz.sig`
- `artifortress-0.1.0-rc.2.tgz.crt`
- `OCI-IMAGES.txt`
- `SHA256SUMS`
