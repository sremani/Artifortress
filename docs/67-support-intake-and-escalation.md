# Support Intake And Escalation Model

Last updated: 2026-04-27

## Purpose

This document defines the customer-facing support intake, severity, and
escalation model for Kublai enterprise releases.

It sets support expectations. It does not replace a customer-specific contract
or SLA.

## Support Intake Requirements

Every support request should include:

- environment name
- release version or git commit
- customer-visible impact
- severity requested
- incident start time and timezone
- recent deployments, migrations, identity changes, storage changes, or network
  changes
- support bundle archive from `scripts/support-bundle.sh`
- whether rollback, failover, or scaling actions have already been attempted
- whether production traffic is affected

Do not include:

- admin bearer tokens
- raw database dumps
- raw object-storage contents
- private IdP signing keys
- plaintext object-storage credentials

## Severity Levels

| Severity | Definition | Examples | Initial Response Target |
|---|---|---|---:|
| `SEV-1` | Production outage or active data exposure risk. | production `not_ready`, suspected cross-tenant data exposure, destructive lifecycle defect affecting legal hold | 1 hour |
| `SEV-2` | Major production degradation with workaround or partial availability. | persistent `degraded`, upload/download partial outage, growing worker backlog with business impact | 4 hours |
| `SEV-3` | Non-critical production issue or release-blocking staging issue. | failed pre-release drill, isolated API defect, documentation gap blocking rollout | 1 business day |
| `SEV-4` | Question, enhancement request, or low-impact defect. | how-to question, cosmetic docs issue, future feature request | 3 business days |

Response targets are planning targets for the support model. Contractual SLAs
must be defined separately if required.

## Severity Classification Rules

Classify as `SEV-1` when:

- production API is `not_ready`
- artifact upload/download is unavailable for all users
- legal hold or retention controls may have failed destructively
- cross-tenant access or data exposure is suspected
- release provenance failure affects a promoted production release

Classify as `SEV-2` when:

- production is `degraded` and business workflows are at risk
- async backlog is growing and not recovering
- search is stale beyond operational tolerance
- one dependency is unstable but workaround exists
- restore or rollback path is uncertain during an active incident

Classify as `SEV-3` when:

- staging or release candidate validation fails
- enterprise verification fails outside production
- customer has a reproducible defect without active production impact
- documentation is insufficient for a planned operation

Classify as `SEV-4` when:

- there is no incident or blocked rollout
- request is informational
- request is for roadmap or unsupported feature consideration

## Escalation Path

1. Support intake confirms severity and required evidence.
2. Support reviews support bundle and diagnostic catalog.
3. Engineering is engaged for confirmed product defects, data integrity risk, or
   failed release gates.
4. Security is engaged immediately for suspected data exposure, secret leakage,
   auth bypass, or provenance failure.
5. Operations/release owner is engaged for upgrade, rollback, or deployment
   certification issues.

## Required Evidence By Severity

| Severity | Required Evidence |
|---|---|
| `SEV-1` | support bundle, readiness output, ops summary, timeline, rollback/failover state |
| `SEV-2` | support bundle, affected workflow, recent changes, dashboard or backlog signals |
| `SEV-3` | reproduction steps, expected/actual behavior, release candidate evidence if applicable |
| `SEV-4` | question or request, environment context if relevant |

## Communication Cadence

Recommended cadence:

- `SEV-1`: status update every hour until mitigated
- `SEV-2`: status update every business day or when material state changes
- `SEV-3`: update when triage completes and when fix/mitigation is planned
- `SEV-4`: update when answer or disposition is available

## Escalation Triggers

Escalate severity when:

- impact expands to production
- readiness changes from `degraded` to `not_ready`
- suspected data exposure appears plausible
- legal hold or audit integrity is at risk
- workaround fails
- release candidate blocker affects a committed customer launch date

De-escalate only when:

- customer-visible impact is mitigated
- readiness and ops summary are stable
- rollback/failover is complete
- evidence shows no data exposure or destructive lifecycle issue

## Closure Criteria

Close a support request only when:

- customer impact is resolved or accepted
- root cause or best-known cause is documented
- mitigation or fix is documented
- follow-up tickets exist for remaining product gaps
- customer has validated the outcome or accepted closure

For `SEV-1` and `SEV-2`, closure should also include:

- timeline
- contributing factors
- detection gap, if any
- prevention or hardening follow-up

## Related Documents

- `docs/65-support-bundle-workflow.md`
- `docs/66-diagnostic-error-catalog.md`
- `docs/31-operations-howto.md`
- `docs/42-operations-dashboard-and-drill-bundle.md`
- `docs/62-enterprise-security-whitepaper.md`
