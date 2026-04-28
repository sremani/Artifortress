# Release Provenance Evidence

Generated at: 2026-04-28T21:18:38Z

## Summary

- overall status: PASS
- tag: v0.1.0-rc.1
- version: 0.1.0-rc.1
- tag subject: Artifortress v0.1.0-rc.1
- tag commit: 92e9dd61e1a22ba4377c35a27ff76edb6ebbd1e8
- asset directory: `/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1`
- started at: 2026-04-28T21:18:31Z
- ended at: 2026-04-28T21:18:38Z
- total duration seconds: 7

## Verified OCI Images

- API: `ghcr.io/sremani/artifortress-api@sha256:a5e699b0d70cbe19d54eaf273fd0ebad1a6bbc59aaf93037fed42e318bfb77df`
- Worker: `ghcr.io/sremani/artifortress-worker@sha256:9352bf85628468d66efd323466c482bff2dbc4de2c5f7fc47508bd7a37fc0536`

## Verification Steps

| Step | Command | Status | Duration Seconds | Log |
|---|---|---:|---:|---|
| fetch release tag | `cd '/home/srikanth/projects/Artifortress' && git fetch origin 'refs/tags/v0.1.0-rc.1:refs/tags/v0.1.0-rc.1'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.1/fetch-release-tag.log` |
| verify signed git tag | `cd '/home/srikanth/projects/Artifortress' && git verify-tag 'v0.1.0-rc.1'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/verify-signed-git-tag.log` |
| check remote tag | `cd '/home/srikanth/projects/Artifortress' && git ls-remote --exit-code --tags origin 'refs/tags/v0.1.0-rc.1'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.1/check-remote-tag.log` |
| check GitHub release | `cd '/home/srikanth/projects/Artifortress' && gh release view 'v0.1.0-rc.1'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/check-GitHub-release.log` |
| download release assets | `cd '/home/srikanth/projects/Artifortress' && gh release download 'v0.1.0-rc.1' --dir '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' --clobber` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.1/download-release-assets.log` |
| verify checksums | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' checksum SHA256SUMS` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/verify-checksums.log` |
| verify artifortress-api-linux-x64.tar.gz signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-api-linux-x64.tar.gz' 'artifortress-api-linux-x64.tar.gz.sig' 'artifortress-api-linux-x64.tar.gz.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/verify-artifortress-api-linux-x64-tar-gz-signature.log` |
| verify artifortress-worker-linux-x64.tar.gz signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-worker-linux-x64.tar.gz' 'artifortress-worker-linux-x64.tar.gz.sig' 'artifortress-worker-linux-x64.tar.gz.crt'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.1/verify-artifortress-worker-linux-x64-tar-gz-signature.log` |
| verify artifortress-api.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-api.sbom.cdx.json' 'artifortress-api.sbom.cdx.json.sig' 'artifortress-api.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/verify-artifortress-api-sbom-cdx-json-signature.log` |
| verify artifortress-worker.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-worker.sbom.cdx.json' 'artifortress-worker.sbom.cdx.json.sig' 'artifortress-worker.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/verify-artifortress-worker-sbom-cdx-json-signature.log` |
| verify artifortress-api-image.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-api-image.sbom.cdx.json' 'artifortress-api-image.sbom.cdx.json.sig' 'artifortress-api-image.sbom.cdx.json.crt'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.1/verify-artifortress-api-image-sbom-cdx-json-signature.log` |
| verify artifortress-worker-image.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-worker-image.sbom.cdx.json' 'artifortress-worker-image.sbom.cdx.json.sig' 'artifortress-worker-image.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/verify-artifortress-worker-image-sbom-cdx-json-signature.log` |
| verify artifortress-helm-chart.sbom.cdx.json signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-helm-chart.sbom.cdx.json' 'artifortress-helm-chart.sbom.cdx.json.sig' 'artifortress-helm-chart.sbom.cdx.json.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/verify-artifortress-helm-chart-sbom-cdx-json-signature.log` |
| verify artifortress-0.1.0-rc.1.tgz signature | `cd '/home/srikanth/projects/Artifortress/artifacts/release-provenance/v0.1.0-rc.1' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' blob 'artifortress-0.1.0-rc.1.tgz' 'artifortress-0.1.0-rc.1.tgz.sig' 'artifortress-0.1.0-rc.1.tgz.crt'` | PASS | 0 | `artifacts/release-provenance/v0.1.0-rc.1/verify-artifortress-0-1-0-rc-1-tgz-signature.log` |
| verify API image signature | `cd '/home/srikanth/projects/Artifortress' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' image 'ghcr.io/sremani/artifortress-api@sha256:a5e699b0d70cbe19d54eaf273fd0ebad1a6bbc59aaf93037fed42e318bfb77df'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.1/verify-API-image-signature.log` |
| verify worker image signature | `cd '/home/srikanth/projects/Artifortress' && '/home/srikanth/projects/Artifortress/scripts/verify-release-artifacts.sh' image 'ghcr.io/sremani/artifortress-worker@sha256:9352bf85628468d66efd323466c482bff2dbc4de2c5f7fc47508bd7a37fc0536'` | PASS | 1 | `artifacts/release-provenance/v0.1.0-rc.1/verify-worker-image-signature.log` |

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
- `artifortress-0.1.0-rc.1.tgz`
- `artifortress-0.1.0-rc.1.tgz.sig`
- `artifortress-0.1.0-rc.1.tgz.crt`
- `OCI-IMAGES.txt`
- `SHA256SUMS`
