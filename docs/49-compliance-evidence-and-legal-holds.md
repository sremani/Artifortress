# Compliance Evidence and Legal Holds

Last updated: 2026-04-07

## Scope

This document closes `ER-5-04` by making compliance evidence and legal-hold
controls first-class operator workflows.

The implemented posture is:

- active `legal_hold` protections are visible through the API
- active holds prevent GC from hard-deleting tombstoned package versions
- tenant auditors can generate a self-contained compliance evidence pack without
  direct database access

## Legal Hold Controls

Endpoints:

- `POST /v1/repos/{repoKey}/packages/versions/{versionId}/protection`
  - set `mode` to `legal_hold`
- `POST /v1/repos/{repoKey}/packages/versions/{versionId}/protection/release`
  - releases the active hold or protection
- `GET /v1/compliance/legal-holds`
  - lists active legal holds for the tenant

Legal-hold behavior:

- a package version under active `legal_hold` cannot be tombstoned if the hold
  already exists
- if a legal hold is applied after tombstoning, GC will not hard-delete the
  version while the hold remains active
- blobs referenced by held versions remain rooted and are not reclaimed as GC
  candidates

This closes the lifecycle gap where retention expiry alone could otherwise
delete a tombstoned version still subject to a compliance hold.

## Compliance Evidence Pack

Endpoint:

- `GET /v1/compliance/evidence`

Access:

- requires `tenant_auditor` or platform admin-equivalent access

Query parameters:

- `auditLimit`
  - default `500`, max `2000`
- `approvalLimit`
  - default `250`, max `1000`

Response characteristics:

- content type: `application/json`
- `Content-Disposition: attachment; filename=compliance-evidence-pack.json`
- `X-Kublai-Compliance-Pack-SHA256`
  - SHA-256 digest of the returned JSON payload

Included evidence:

- tenant governance policy snapshot
- tenant role bindings
- active legal holds
- recent governance approvals
- recent audit records
- NDJSON audit export plus SHA-256 digest

The endpoint also emits an audit event:

- `compliance.evidence.generated`

## Example

```bash
curl -sS \
  -H "Authorization: Bearer $TOKEN" \
  "https://kublai.example.com/v1/compliance/evidence?auditLimit=500&approvalLimit=250" \
  -D /tmp/compliance-headers.txt \
  -o /tmp/compliance-evidence-pack.json
```

Then verify the digest in `X-Kublai-Compliance-Pack-SHA256` against the
saved payload.

## Acceptance Notes

`ER-5-04` acceptance criteria are now satisfied because:

- legal-hold lifecycle behavior is enforced through application code rather than
  manual SQL intervention
- auditors can produce a structured evidence pack directly from supported APIs
- regression tests cover both hold-aware GC behavior and evidence generation
