# Admin CLI Operator Workflows

Last updated: 2026-04-28

## Purpose

`Kublai.AdminCli` is the supported command-line wrapper for routine
operator workflows. It maps to the public API, keeps output JSON-first for
automation, and redacts response fields whose names indicate secrets.

The CLI does not bypass API authorization or perform direct database repair.

## Build And Run

```bash
make build
make admin-cli ARGS="--url https://kublai.example.com health ready"
```

Global environment variables:

- `KUBLAI_URL`
- `KUBLAI_TOKEN`
- `KUBLAI_BOOTSTRAP_TOKEN`
- `KUBLAI_CORRELATION_ID`
- `KUBLAI_GOVERNANCE_APPROVAL_ID`

## Common Workflows

Initial bounded administrative PAT:

```bash
make admin-cli ARGS="--url https://kublai.example.com --bootstrap-token <bootstrap-token> auth issue-pat --subject platform-admin --scope repo:*:admin --ttl-minutes 60"
```

Daily readiness and operations preflight:

```bash
KUBLAI_URL=https://kublai.example.com \
KUBLAI_TOKEN=<admin-token> \
make admin-cli ARGS="preflight"
```

Repository administration:

```bash
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> repo create --repo libs-release --type local"
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> repo bindings set --repo libs-release --subject team-builds --role write"
```

PAT and tenant administration:

```bash
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> auth pats list"
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> tenant roles set --subject auditor@example.com --role tenant_auditor"
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> tenant roles delete --subject departing-admin@example.com"
```

Tenant lifecycle:

```bash
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> tenant lifecycle mark --step offboarding.started --status started --reason contract ended"
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> tenant offboarding-readiness"
```

Compliance, legal hold, and evidence:

```bash
make admin-cli ARGS="--url https://kublai.example.com --token <auditor-token> compliance legal-holds"
make admin-cli ARGS="--url https://kublai.example.com --token <auditor-token> compliance evidence --audit-limit 500 --approval-limit 250"
```

GC, search, and reconciliation:

```bash
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> gc run --batch-size 250"
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> gc run --execute --retention-grace-hours 24 --batch-size 250"
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> search status"
make admin-cli ARGS="--url https://kublai.example.com --token <admin-token> reconcile blobs"
```

## Output Contract

- successful API responses are printed to stdout as JSON
- API error responses are printed to stdout as redacted JSON and return a
  non-zero exit code
- CLI argument errors are printed to stderr and return exit code `2`
- response properties such as `token`, `secret`, `password`, `authorization`,
  and `samlResponse` are replaced with `[REDACTED]`
- identifier fields such as `tokenId` are preserved

## Smoke Coverage

`tests/Kublai.Domain.Tests/AdminCliSmokeTests.fs` covers the highest-risk
paths:

- initial PAT issuance uses bootstrap-token auth and redacts returned secrets
- GC defaults to dry-run unless `--execute` is supplied
- preflight combines anonymous readiness with authenticated ops summary
- tenant lifecycle markers and offboarding readiness map to supported endpoints
- tenant role delete maps to the access-revocation endpoint
