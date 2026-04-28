# Release Provenance Evidence

Generated at: not generated

## Summary

- overall status: NOT_RUN
- tag: pending
- version: pending
- reason: no release-candidate tag has been created for EGA-13 yet

## Current Gate

The certification command is available:

```bash
make release-provenance-certify TAG=v<version>
```

The real run is blocked until:

- the enterprise GA work is committed to the release-candidate branch
- git tag signing is configured
- a signed `v<version>` tag is pushed to GitHub
- the `release-provenance` workflow completes

This file is overwritten by `scripts/release-provenance-certify.sh` after a
real tag-triggered release run.
