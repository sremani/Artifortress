# Kublai Branding And Rename Policy

Last updated: 2026-04-28

## Purpose

Kublai is the product, codebase, documentation, deployment, and release brand.
Source-controlled materials should use Kublai naming consistently.

## Naming Rules

| Surface | Required Name |
| --- | --- |
| Product name | Kublai |
| Lower-case resource prefix | `kublai` |
| Environment variables | `KUBLAI_*` |
| .NET namespaces | `Kublai.*` |
| Project directories | `src/Kublai.*`, `tools/Kublai.*`, `tests/Kublai.*` |
| Solution file | `Kublai.sln` |
| Helm chart | `deploy/helm/kublai` with chart name `kublai` |
| Kubernetes app labels | `kublai-api`, `kublai-worker`, and `part-of: kublai` |
| OCI image examples | `kublai-api` and `kublai-worker` |
| Runtime database examples | `kublai`, `kublai_staging`, or environment-specific variants |
| Public examples | `kublai.example.com` until a production domain is finalized |

## Generated Artifacts

Generated build outputs under `bin` and `obj`, downloaded release artifacts
under `artifacts`, `.git` metadata, and diagnostic trace files are not treated
as source-controlled naming policy. They may contain historical names until
rebuilt, regenerated, or replaced by the next release workflow.

## Validation

`make test` runs unit coverage that scans source-controlled content and paths
for legacy brand tokens. New source, documentation, scripts, chart files, and
deployment examples should fail that test if they reintroduce stale naming.
