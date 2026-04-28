# Security Policy

## Supported Versions

Artifortress enterprise security support follows
`docs/60-versioning-support-policy.md`.

Supported lines:

- current `MAJOR.MINOR` release line
- immediately previous `MAJOR.MINOR` release line for critical security and
  migration support
- release candidates for evaluation only, unless explicitly promoted in the
  release sign-off board

## Reporting A Vulnerability

Do not report suspected vulnerabilities in public issues, discussions, or
public pull requests.

Preferred private channels:

- GitHub private vulnerability reporting for this repository, when enabled
- `security@artifortress.com`, once the mailbox is provisioned
- maintainer escalation through the enterprise support intake path in
  `docs/67-support-intake-and-escalation.md`

Include:

- affected Artifortress version, tag, or commit
- deployment shape, if relevant
- vulnerability class and suspected impact
- reproduction steps or proof of concept
- whether exploitation is known or suspected
- reporter contact and disclosure preference

Do not include:

- production secrets
- raw customer data
- private signing keys
- database dumps
- object-storage contents

## Response Targets

These are planning targets unless a customer contract says otherwise.

| Severity | Acknowledgement | Triage Target | Fix Or Mitigation Target |
|---|---:|---:|---:|
| Critical | 24 hours | 72 hours | 7 calendar days |
| High | 2 business days | 5 business days | 30 calendar days |
| Medium | 5 business days | 10 business days | 90 calendar days |
| Low | 10 business days | next planning cycle | next practical release |

Actively exploited critical issues may receive an emergency mitigation,
temporary disablement, or narrow patch before full release documentation is
complete.

## Disclosure

Artifortress follows coordinated vulnerability disclosure.

Advisories should include:

- affected versions
- fixed versions
- severity and impact summary
- mitigation or upgrade path
- release provenance links
- credit, if the reporter wants attribution

See `docs/75-vulnerability-disclosure-and-patch-sla.md` for the full policy.
