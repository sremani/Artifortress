# Enterprise GA Ticket Board

Last updated: 2026-04-28

## Purpose

This board turns the existing enterprise-hardening work into a productization
plan for an enterprise-ready Kublai release.

The repository already has substantial enterprise engineering closure:

- security, identity, trust-material rotation, PAT governance, and release
  provenance are documented as closed
- tenant isolation, governance, legal holds, audit export, and compliance
  evidence APIs are present
- HA topology, dependency failure semantics, upgrade compatibility, reliability
  drills, and performance soak guides exist
- deployment assets include Docker Compose, Kubernetes manifests, Helm, Grafana,
  alert thresholds, and environment templates

The remaining gap is not primarily core correctness. The remaining gap is the
difference between a hardened engineering repository and a product that a large
organization can evaluate, procure, install, operate, upgrade, audit, and get
support for with low ambiguity.

## Enterprise Ready Definition

Kublai is enterprise ready when a target customer can complete all of the
following without direct maintainer intervention:

1. evaluate the security model, support envelope, and architecture
2. install a signed release into a supported production shape
3. connect enterprise identity and tenant administration
4. run backup, restore, upgrade, rollback, and incident drills
5. export audit/compliance evidence for review
6. operate dashboards and alerts from documented SLOs
7. diagnose common failures and produce a support bundle
8. upgrade between supported versions under a published lifecycle policy

## Priority Key

- `P0`: enterprise launch blocker
- `P1`: required before first paid production customer
- `P2`: required before broad enterprise rollout
- `P3`: follow-up hardening or adoption accelerator

## Status Key

- `todo`: not started
- `in_progress`: started but not launch-ready
- `done`: implemented and validated
- `blocked`: waiting on a decision or dependency
- `deferred`: intentionally outside the current launch

## Ticket Board

| Ticket | Title | Priority | Area | Status |
|---|---|---:|---|---|
| EGA-01 | Define supported enterprise product envelope | P0 | Product/Support | done |
| EGA-02 | Produce enterprise security whitepaper | P0 | Security/Procurement | done |
| EGA-03 | Produce administrator handbook | P0 | Documentation/Ops | done |
| EGA-04 | Produce installation and production cutover guide | P0 | Deployment | done |
| EGA-05 | Publish versioning, deprecation, and support-window policy | P0 | Release Management | done |
| EGA-06 | Add release candidate checklist and GA sign-off board | P0 | Release Management | done |
| EGA-07 | Publish signed container images and Helm chart artifacts | P0 | Distribution | done |
| EGA-08 | Add Helm install/upgrade/uninstall certification job | P0 | Deployment/CI | done |
| EGA-09 | Add production preflight validator | P0 | Deployment/Ops | done |
| EGA-10 | Add support bundle collection workflow | P0 | Supportability | done |
| EGA-11 | Define SLOs, SLIs, and alert routing guidance | P0 | Operations | done |
| EGA-12 | Validate HA deployment in Kubernetes reference environment | P0 | Reliability | done |
| EGA-13 | Run release provenance on a real signed tag | P0 | Supply Chain | done |
| EGA-14 | Resolve GitHub Actions Node 20 deprecation risk | P0 | CI/Supply Chain | done |
| EGA-31 | Persist consolidated enterprise verification evidence | P0 | Release Evidence | done |
| EGA-32 | Define kublai.com production hosting hardware plan | P0 | Launch Infrastructure | in_progress |
| EGA-33 | Remove MinIO as strategic object-storage dependency | P0 | Storage/Launch Risk | in_progress |
| EGA-34 | Complete Kublai deep rebrand | P0 | Product/Branding | done |
| EGA-34T | Validate Kublai rebrand coverage | P0 | Validation | done |
| EGA-15 | Create procurement evidence pack | P1 | Procurement/Compliance | done |
| EGA-16 | Map controls to SOC 2-style evidence | P1 | Compliance | done |
| EGA-17 | Add vulnerability disclosure and patch SLA policy | P1 | Security/Support | done |
| EGA-18 | Add diagnostic error catalog and operator playbooks | P1 | Supportability | done |
| EGA-19 | Add admin CLI for common operator workflows | P1 | UX/Ops | done |
| EGA-20 | Add tenant onboarding and offboarding workflow | P1 | Tenant Ops | done |
| EGA-21 | Add cloud-specific production examples | P1 | Deployment | done |
| EGA-21T | Validate cloud-specific production examples | P1 | Validation | done |
| EGA-22 | Add air-gapped/offline install plan | P1 | Distribution | done |
| EGA-22T | Validate air-gapped/offline install plan | P1 | Validation | done |
| EGA-23 | Certify backup/restore and upgrade drills against release artifacts | P1 | Reliability | in_progress |
| EGA-23T | Add release-artifact drill validation evidence | P1 | Validation | in_progress |
| EGA-24 | Publish capacity certification on non-local infrastructure | P1 | Performance | todo |
| EGA-25 | Add package-format compatibility strategy | P1 | Product/API | done |
| EGA-25T | Validate package-format compatibility strategy | P1 | Validation | done |
| EGA-26 | Add enterprise trial/evaluation path | P2 | Product | todo |
| EGA-27 | Add support intake, severity, and escalation model | P2 | Support | done |
| EGA-28 | Add customer-facing migration guide from incumbent repositories | P2 | Adoption | todo |
| EGA-29 | Add reference Terraform/OpenTofu deployment module | P2 | Deployment | todo |
| EGA-30 | Add disaster recovery expansion plan for multi-region | P2 | Reliability | deferred |

## Ticket Details

### EGA-01: Define supported enterprise product envelope

Scope:
- Define what Kublai supports at GA and what it explicitly does not.
- Include supported deployment topology, dependency versions, scale profiles,
  package/API surface, identity modes, compliance posture, and upgrade paths.
- Convert unsupported claims from existing docs into a customer-facing support
  boundary.

Acceptance criteria:
- A single document states the supported enterprise envelope.
- Every production-facing guide links back to the envelope.
- Unsupported items are clear enough for sales, support, and operators to use
  without interpretation.

Status:
- done in `docs/59-enterprise-product-envelope.md`

### EGA-02: Produce enterprise security whitepaper

Scope:
- Summarize identity, RBAC, tenant isolation, audit, legal hold, provenance,
  secret redaction, and threat model closure.
- Reference implemented evidence from security review closure and compliance
  docs.
- Include deployment security assumptions for Postgres, object storage, TLS,
  ingress, key material, and admin tokens.

Acceptance criteria:
- Security reviewers can evaluate the product without reading source code.
- Known residual risks and out-of-scope claims are stated plainly.
- Whitepaper maps each security claim to evidence or implementation artifacts.

Status:
- done in `docs/62-enterprise-security-whitepaper.md`

### EGA-03: Produce administrator handbook

Scope:
- Combine day-0, day-1, and day-2 operations into one admin guide.
- Include identity setup, tenant administration, PAT governance, repository
  administration, audit export, legal holds, GC, search rebuilds, backups,
  restores, and incident response.

Acceptance criteria:
- A new operator can run routine workflows using supported APIs/scripts.
- High-risk workflows include prerequisites, rollback notes, and audit effects.
- Handbook avoids relying on direct database intervention for normal operation.

Status:
- done in `docs/71-administrator-handbook.md`

### EGA-04: Produce installation and production cutover guide

Scope:
- Create an end-to-end install path from release artifacts to production
  readiness.
- Include namespace setup, secrets, Helm values, ingress/TLS, readiness gates,
  initial admin bootstrap, identity hookup, dashboard import, smoke tests, and
  cutover criteria.

Acceptance criteria:
- The guide starts from a clean cluster and ends with a ready Kublai
  deployment.
- Cutover cannot be declared until readiness, ops summary, smoke test, and
  backup evidence gates pass.
- The guide links to rollback and incident procedures.

Status:
- done in `docs/72-installation-and-production-cutover-guide.md`

### EGA-05: Publish versioning, deprecation, and support-window policy

Scope:
- Define SemVer use, supported minor versions, schema compatibility policy,
  security patch policy, deprecation notice periods, and minimum supported
  dependency versions.

Acceptance criteria:
- Customers know which versions are supported and for how long.
- Breaking-change and migration expectations are explicit.
- Release notes can be evaluated against a published policy.

Status:
- done in `docs/60-versioning-support-policy.md`

### EGA-06: Add release candidate checklist and GA sign-off board

Scope:
- Define the required evidence for each release candidate.
- Include CI, integration tests, enterprise verification, upgrade drill,
  reliability drill, provenance verification, chart install certification,
  security sign-off, and docs sign-off.

Acceptance criteria:
- A release cannot be labeled enterprise GA without a complete checklist.
- Checklist artifacts are reproducible and referenced by commit/tag.
- Sign-off records show owner, date, result, and exception disposition.

Status:
- done in `docs/61-release-candidate-signoff.md`

### EGA-07: Publish signed container images and Helm chart artifacts

Scope:
- Extend release provenance beyond tarballs to OCI images and Helm chart
  packages.
- Sign images and charts, generate SBOMs, and publish verification steps.

Acceptance criteria:
- API and worker images are pushed with immutable version tags and digests.
- Helm chart is packaged and versioned alongside the app release.
- Image/chart signatures and SBOMs verify using documented commands.

Status:
- done in `.github/workflows/release-provenance.yml`
- verification documented in `docs/44-release-provenance-and-verification.md`
- verification helper extended in `scripts/verify-release-artifacts.sh`

### EGA-08: Add Helm install/upgrade/uninstall certification job

Scope:
- Add CI or release workflow coverage that installs the chart into a real test
  cluster, validates readiness, runs smoke tests, performs upgrade from the
  previous supported version, and uninstalls cleanly.

Acceptance criteria:
- Helm chart regressions fail before release.
- Upgrade behavior is tested against the published support matrix.
- Uninstall leaves no unexpected namespaced resources except documented data
  dependencies.

Status:
- done in `scripts/helm-certify.sh`
- CI workflow added in `.github/workflows/helm-certification.yml`
- repeatable command: `make helm-certify`
- latest evidence path: `docs/reports/helm-certification-latest.md`

### EGA-09: Add production preflight validator

Scope:
- Add a script or CLI command that validates environment readiness before
  cutover.
- Check required secrets, database connectivity, object storage connectivity,
  ingress/TLS assumptions, identity trust material, schema status, bucket
  configuration, and dashboard/alert configuration.

Acceptance criteria:
- Preflight emits deterministic pass/fail output.
- Failures name the exact missing or unsafe condition.
- Production cutover guide requires a passing preflight result.

Status:
- done in `scripts/production-preflight.sh`
- workflow documented in `docs/70-production-preflight.md`

### EGA-10: Add support bundle collection workflow

Scope:
- Add a support bundle script or endpoint for operators to collect diagnostics
  without exposing secrets.
- Include version, commit, config shape with redaction, readiness state, ops
  summary, recent audit metadata, migration state, selected logs, and drill
  report pointers.

Acceptance criteria:
- Support can triage common failures from a single bundle.
- Bundle output is redacted by default.
- Tests or fixtures verify that secret-like values are not emitted.

Status:
- done in `scripts/support-bundle.sh`
- workflow documented in `docs/65-support-bundle-workflow.md`

### EGA-11: Define SLOs, SLIs, and alert routing guidance

Scope:
- Turn dashboard metrics into customer-facing reliability objectives.
- Define availability, readiness, upload/download latency, async backlog age,
  search freshness, and restore-time objectives.
- Provide page/warn routing guidance.

Acceptance criteria:
- Operators know which alerts page immediately and which create tickets.
- SLOs are tied to existing metrics or explicitly marked as future metrics.
- Incident playbooks reference the relevant SLI and recovery target.

Status:
- done in `docs/68-slo-sli-alerting.md`

### EGA-12: Validate HA deployment in Kubernetes reference environment

Scope:
- Exercise the documented HA topology in a Kubernetes environment with multiple
  API and worker replicas.
- Validate readiness gating, worker scaling, job lease recovery, rolling restart,
  dependency outage behavior, and ingress smoke tests.

Acceptance criteria:
- Evidence report is committed under `docs/reports`.
- The report includes cluster shape, commands, results, failures, and residual
  risks.
- Production support envelope is updated to match what was actually validated.

Status:
- done with validation plan in `docs/69-ha-kubernetes-validation-plan.md`
- latest evidence: `docs/reports/ha-kubernetes-validation-latest.md`
- repeatable command: `make kind-ha-validate`

### EGA-13: Run release provenance on a real signed tag

Scope:
- Create a release candidate tag and exercise `.github/workflows/release-provenance.yml`.
- Verify downloaded artifacts using the documented verification helper.

Acceptance criteria:
- Release provenance evidence references a real tag, checksums, signatures, and
  SBOMs.
- Any manual gaps in the release process become tickets before GA.

Status:
- done for signed tag `v0.1.0-rc.2`
- release workflow run: `25079528537`
- certification helper:
  `scripts/release-provenance-certify.sh`
- repeatable command: `make release-provenance-certify TAG=v<version>`
- latest evidence: `docs/reports/release-provenance-latest.md`
- prior Node 20 warning from `v0.1.0-rc.1` closed by `EGA-14`

### EGA-14: Resolve GitHub Actions Node 20 deprecation risk

Scope:
- Upgrade workflow actions that still rely on deprecated Node 20 runtimes.
- Confirm CI, mutation, and release provenance workflows remain green.

Acceptance criteria:
- No release-blocking hosted-runner deprecation warnings remain.
- Workflows continue to pass after action upgrades.
- Release provenance workflow is included in validation.

Status:
- done
- evidence report: `docs/reports/github-actions-node24-validation-latest.md`
- upgraded `actions/checkout`, `actions/setup-dotnet`, `actions/cache`,
  `actions/upload-artifact`, and `softprops/action-gh-release` to current
  Node 24-compatible major versions
- validation runs:
  - CI: `25079146581`
  - Helm certification: `25079049857`
  - mutation: `25079314883`
  - release provenance: `25079528537`
- validation release: signed tag `v0.1.0-rc.2`

### EGA-31: Persist consolidated enterprise verification evidence

Scope:
- Extend `scripts/verify-enterprise.sh` so the enterprise verification battery
  writes a top-level report artifact under `docs/reports`.
- Capture the executed commit, SDK version, start/end timestamps, command list,
  pass/fail status for each step, and links to generated drill/baseline reports.
- Preserve the existing fail-fast behavior while still writing enough failure
  context for release review.

Rationale:
- The current verification battery validates the right paths, but the top-level
  result is only emitted to stdout.
- Enterprise GA sign-off needs durable evidence tying unit tests, integration
  tests, load baseline, backup/restore, upgrade compatibility, reliability,
  search soak, and performance soak into one release-candidate record.

Acceptance criteria:
- `make verify-enterprise` produces
  `docs/reports/enterprise-verification-latest.md`.
- The report includes status, timestamps, commit, SDK/runtime version, and each
  verification step.
- The report links to or names all generated subreports.
- A failed step records the failing command and status before the script exits.

Status:
- done in `scripts/verify-enterprise.sh`
- latest evidence: `docs/reports/enterprise-verification-latest.md`

### EGA-32: Define kublai.com production hosting hardware plan

Scope:
- Select the launch hosting shape for `kublai.com`.
- Define Dev, PreProd, and Prod compute, database, object storage, ingress,
  Cloudflare, observability, backup, and DNS/TLS requirements.
- Estimate monthly run cost and identify account/provider decisions that must be
  made before purchase.
- Tie the hosting shape back to the supported product envelope, production
  cutover guide, production preflight, and release sign-off gates.

Acceptance criteria:
- A production hosting plan exists with recommended node count, node size,
  database class, object-storage capacity, load balancer, Cloudflare settings,
  monitoring, and backup posture.
- The plan includes budgetary cost ranges and provider pricing references.
- Dev, PreProd, and Prod are separated by namespace, database, bucket, DNS, and
  credentials.
- Cutover to `kublai.com` is blocked until preflight, smoke tests,
  backup/restore evidence, release provenance, and rollback ownership are
  complete.
- Open provider/account decisions are listed explicitly.

Status:
- in_progress with initial plan in
  `docs/73-kublai-com-production-hosting-plan.md`
- preferred candidate updated to Akamai Cloud/Linode with Cloudflare fronting
  production traffic
- environment topology added for Dev, PreProd, and Prod isolation

### EGA-33: Remove MinIO as strategic object-storage dependency

Scope:
- Stop treating MinIO community artifacts as a safe long-term default dependency.
- Define the immediate `kublai.com` managed object-storage path.
- Identify every local, kind, Helm, and documentation path that still depends on
  MinIO.
- Create a side-project charter for an Kublai-owned S3-compatible object
  store if maintained third-party options are not acceptable.

Acceptance criteria:
- Production deployment guidance recommends managed S3-compatible object
  storage for `kublai.com`.
- MinIO usage in local validation is documented as temporary test
  infrastructure.
- A compatibility/conformance plan exists for object-storage providers.
- A side-project decision record exists for a potential Kublai-owned
  replacement.
- Follow-up implementation tickets exist before removing MinIO from validation
  scripts.

Status:
- in_progress with exit plan in
  `docs/74-object-storage-independence-and-minio-exit-plan.md`

### EGA-15: Create procurement evidence pack

Scope:
- Assemble security whitepaper, architecture overview, compliance controls,
  SBOM/provenance docs, support policy, deployment model, data-flow summary,
  subprocessors/dependencies, and residual risk statement.

Acceptance criteria:
- A procurement/security-review folder or document bundle exists.
- Each common questionnaire category has a prepared answer or linked artifact.
- Evidence does not depend on private maintainer knowledge.

Status:
- done in `docs/63-procurement-evidence-pack.md`

### EGA-16: Map controls to SOC 2-style evidence

Scope:
- Create a control mapping for security, availability, confidentiality, change
  management, access control, audit logging, incident response, and vendor risk.

Acceptance criteria:
- Each control maps to code, docs, workflow evidence, or an explicit gap.
- Gaps become follow-up tickets with owners.
- Evidence is versioned with the release.

Status:
- done in `docs/64-soc2-control-mapping.md`

### EGA-17: Add vulnerability disclosure and patch SLA policy

Scope:
- Define how external reporters submit vulnerabilities.
- Define severity classes, response targets, patch timelines, and advisory
  publication process.

Acceptance criteria:
- `SECURITY.md` or equivalent exists.
- Release support policy references vulnerability handling.
- Critical patch workflow is documented.

Status:
- done in `SECURITY.md`
- detailed policy: `docs/75-vulnerability-disclosure-and-patch-sla.md`
- release support references updated in `docs/60-versioning-support-policy.md`

### EGA-18: Add diagnostic error catalog and operator playbooks

Scope:
- Catalog deterministic API error codes, readiness states, trust-material
  failures, quota/admission errors, GC/legal-hold interactions, and search
  rebuild failures.
- Link each to operator action.

Acceptance criteria:
- Operators can translate common errors into next steps.
- Error catalog includes impact, likely causes, and recovery actions.
- Playbooks reference dashboards, logs, and support bundle contents.

Status:
- done in `docs/66-diagnostic-error-catalog.md`

### EGA-19: Add admin CLI for common operator workflows

Scope:
- Provide a minimal supported CLI wrapper for tenant, repository, PAT,
  compliance evidence, legal hold, GC, search rebuild, ops summary, and preflight
  workflows.

Acceptance criteria:
- Routine admin workflows do not require raw curl commands.
- CLI output is scriptable and redacts secrets.
- CLI has smoke coverage for the highest-risk commands.

Status:
- implemented `tools/Kublai.AdminCli` as the supported API-backed
  operator CLI
- added `make admin-cli ARGS="..."` wrapper
- documented workflows in `docs/76-admin-cli-operator-workflows.md`
- smoke coverage added in
  `tests/Kublai.Domain.Tests/AdminCliSmokeTests.fs`

### EGA-20: Add tenant onboarding and offboarding workflow

Scope:
- Define tenant creation, role binding, identity mapping, quota assignment,
  initial repository setup, evidence export, legal hold review, offboarding, and
  deletion/retention behavior.

Acceptance criteria:
- Tenant lifecycle can be completed from documented workflows.
- Offboarding is retention-aware and legal-hold-safe.
- Audit events clearly identify each tenant lifecycle step.

Status:
- implemented lifecycle audit marker endpoint:
  `POST /v1/admin/tenant-lifecycle/events`
- implemented offboarding readiness endpoint:
  `GET /v1/admin/tenant-lifecycle/offboarding-readiness`
- added role-binding delete operations for tenant and repository offboarding
- exposed lifecycle and delete operations through `tools/Kublai.AdminCli`
- documented the supported workflow in
  `docs/77-tenant-onboarding-and-offboarding-workflow.md`

### EGA-21: Add cloud-specific production examples

Scope:
- Add reference values and dependency guidance for at least one managed
  Kubernetes target.
- Include managed Postgres, object storage, ingress/TLS, secret management, and
  backup posture.

Acceptance criteria:
- Example config is deployable with documented substitutions.
- Cloud-specific assumptions are separated from generic Helm defaults.
- The support envelope states which parts are examples vs certified targets.

Status:
- added Akamai/Linode LKE example values for PreProd and Prod:
  `deploy/helm/kublai/values-lke-preprod.example.yaml` and
  `deploy/helm/kublai/values-lke-production.example.yaml`
- added external runtime Secret support through Helm `secrets.create` and
  `secrets.existingSecretName`
- documented substitutions, managed dependencies, backup posture, and support
  boundary in `docs/78-cloud-production-examples.md`
- linked the examples from the deployment config reference, product envelope,
  and README documentation map

### EGA-21T: Validate cloud-specific production examples

Scope:
- Add an automated validation path for cloud-specific Helm example values.
- Ensure the examples render without committing runtime secrets.

Acceptance criteria:
- PreProd and Prod cloud example values pass Helm lint.
- PreProd and Prod cloud example values render with the expected external
  Secret reference.
- The validation command is available to operators and CI.

Status:
- added `scripts/helm-cloud-examples-validate.sh`
- added `make helm-cloud-examples-validate`
- wired the validation into the Helm certification workflow

### EGA-22: Add air-gapped/offline install plan

Scope:
- Define how to mirror images, charts, SBOMs, signatures, and dependencies into
  a restricted environment.
- Include offline signature verification and upgrade procedure.

Acceptance criteria:
- Air-gapped customers can install without pulling from public registries during
  deployment.
- Verification still works offline.
- Unsupported offline assumptions are listed explicitly.

Status:
- added `docs/81-airgapped-offline-install-plan.md`
- added `deploy/offline/release-manifest.example.env`
- documented bundle contents, mirroring, offline verification, install,
  upgrade, and unsupported assumptions
- linked the support boundary from the product envelope and README

### EGA-22T: Validate air-gapped/offline install plan

Scope:
- Add a validation path for the offline installation plan.
- Verify that mirrored image, chart, SBOM, signature, and provenance inputs are
  sufficient without public-registry pulls.

Acceptance criteria:
- A documented dry-run checklist exists for offline install validation.
- Offline signature verification is exercised against mirrored artifacts.
- Unsupported offline assumptions are captured as explicit warnings.

Status:
- added `scripts/offline-install-plan-validate.sh`
- added `make offline-install-plan-validate`
- wired the validation into the CI workflow
- validation checks required plan sections and release manifest keys

### EGA-23: Certify backup/restore and upgrade drills against release artifacts

Scope:
- Run existing drills using packaged release artifacts rather than only source
  tree scripts/builds.
- Include app rollback, schema-forward compatibility, restore-based rollback,
  and drill report capture.

Acceptance criteria:
- Drill evidence references release version and artifact digest.
- Results match the published upgrade and rollback policy.
- Any source-tree-only assumptions are removed from production docs.

Status:
- added release artifact metadata fields to `make phase6-drill` and
  `make upgrade-compatibility-drill` reports
- added certification guidance in
  `docs/79-release-artifact-drill-certification.md`
- updated the upgrade and rollback runbook to require artifact metadata
  validation for release certification
- pending: run the drills against a verified release artifact set and capture
  passing evidence before moving this ticket to `done`

### EGA-23T: Add release-artifact drill validation evidence

Scope:
- Add validation evidence that backup/restore and upgrade drills used packaged
  release artifacts instead of local source-tree builds.

Acceptance criteria:
- Drill report records release tag, image digest, chart digest, and SBOM path.
- Restore and upgrade checks are traceable to the same release artifact set.
- CI or operator scripts fail clearly when artifact metadata is missing.

Status:
- added `scripts/release-artifact-drill-validate.sh`
- added `make release-artifact-drill-validate`
- pending: run against release-generated drill reports with non-placeholder
  metadata

### EGA-24: Publish capacity certification on non-local infrastructure

Scope:
- Re-run baseline, mixed workload, and search soak tests in a production-like
  environment.
- Capture API/worker replica count, Postgres sizing, object storage mode, data
  volumes, latency, throughput, queue age, and error rates.

Acceptance criteria:
- Capacity guide distinguishes local calibration from certified profiles.
- At least one certified production-like profile is published.
- Scaling guidance is backed by reproducible report artifacts.

### EGA-25: Add package-format compatibility strategy

Scope:
- Decide which artifact/package protocols are in the enterprise GA support
  envelope.
- Options may include custom API only, generic blob repository, or prioritized
  compatibility tracks such as NuGet, Maven, npm, OCI, or PyPI.

Acceptance criteria:
- Product positioning is explicit: what clients can use on day one and what is
  future work.
- Incompatible or unsupported package manager expectations are not left implied.
- Follow-up protocol tickets exist for any format selected for GA.

Status:
- documented the GA support boundary in
  `docs/80-package-format-compatibility-strategy.md`
- declared day-one support for the Kublai HTTP API and admin CLI only
- listed native package-manager protocols as future compatibility tracks
- added follow-up `PFC-*` protocol tickets and a required compatibility test
  matrix before any future protocol support claim
- linked the strategy from the product envelope, procurement evidence pack, and
  README documentation map

### EGA-25T: Validate package-format compatibility strategy

Scope:
- Add validation criteria for the package-format compatibility strategy.
- Ensure day-one API support and future package-manager protocol claims are
  testable.

Acceptance criteria:
- Strategy includes an explicit test matrix for each supported or deferred
  package format.
- Future protocol tickets include acceptance tests before any GA claim is made.
- Procurement and product-envelope docs link to the same support boundary.

Status:
- validation matrix is defined in
  `docs/80-package-format-compatibility-strategy.md`
- `PFC-02` and `PFC-04` require compatibility/conformance tests before future
  protocol claims
- procurement and product-envelope links now point to the same support boundary

### EGA-26: Add enterprise trial/evaluation path

Scope:
- Define a 30- to 60-minute evaluator path with sample data, identity stub,
  smoke tests, dashboard import, audit export, and teardown.

Acceptance criteria:
- Evaluators can prove the core value without custom maintainer help.
- The path uses the same release artifacts and installation flow as production
  wherever possible.
- Trial caveats are explicit.

### EGA-27: Add support intake, severity, and escalation model

Scope:
- Define support channels, severity levels, response expectations, required
  diagnostics, escalation path, and maintainer handoff procedure.

Acceptance criteria:
- Operators know what to include in a support ticket.
- Support staff know how to classify and escalate incidents.
- Severity policy aligns with the SLO/SLA language.

Status:
- done in `docs/67-support-intake-and-escalation.md`

### EGA-28: Add customer-facing migration guide from incumbent repositories

Scope:
- Document how customers should evaluate migration from existing artifact
  repositories.
- Include inventory, dry run, checksum verification, cutover, rollback, and
  audit evidence expectations.

Acceptance criteria:
- Migration planning has a safe default path.
- Unsupported repository formats and migration methods are explicit.
- The guide links to package-format strategy.

### EGA-29: Add reference Terraform/OpenTofu deployment module

Scope:
- Provide optional infrastructure-as-code for supported cloud dependencies and
  cluster resources.

Acceptance criteria:
- Module is versioned independently or clearly tied to Kublai releases.
- It creates only documented dependencies and values.
- It includes destroy/teardown guidance and state-safety notes.

### EGA-30: Add disaster recovery expansion plan for multi-region

Scope:
- Define future work for multi-region recovery, replication, RPO/RTO targets,
  and unsupported active/active claims.

Acceptance criteria:
- Current GA docs do not imply multi-region active/active support.
- Future multi-region work is scoped as a separate roadmap.
- Customer expectations are bounded before procurement review.

### EGA-34: Complete Kublai deep rebrand

Scope:
- Make Kublai the product, code, documentation, deployment, and release name.
- Cover source namespaces, project files, solution name, test projects, tools,
  documentation, CLI display text, Helm chart metadata, image/release names,
  domain references, support/procurement material, and operator examples.
- Rename source-controlled directories and files to Kublai naming.
- Establish a naming policy for any generated artifacts or external resources.

Acceptance criteria:
- Public-facing docs consistently use Kublai according to an explicit naming
  policy.
- Source-controlled code, docs, scripts, tests, chart files, and deployment
  examples contain no legacy brand references.
- Release, Helm, image, CLI, and domain naming decisions are documented before
  the next public release candidate.
- Procurement, security, support, and install docs agree on the brand name.

Status:
- renamed solution, source projects, test projects, tools, namespaces, Helm
  chart, chart values, deployment examples, scripts, docs, reports, and public
  copy to Kublai naming
- added `docs/82-kublai-branding-and-rename-policy.md`
- retained generated `bin`, `obj`, release artifact cache, and trace output as
  non-source artifacts

### EGA-34T: Validate Kublai rebrand coverage

Scope:
- Add automated checks for public-facing brand consistency.
- Guard against accidental legacy brand references in customer-facing
  documentation, code, scripts, and release surfaces.

Acceptance criteria:
- A validation script or unit test inventories source-controlled content and
  paths for legacy brand references.
- CI fails when public-facing docs introduce unapproved legacy brand references.
- Test coverage asserts the rebrand naming policy exists and is linked from the
  ticket board.

Status:
- added unit coverage in `tests/Kublai.Domain.Tests`
- brand scan excludes generated `bin`, `obj`, `artifacts`, `.git`, and trace
  files
- rebrand validation runs as part of `make test`

## Recommended Execution Order

1. Close P0 product envelope, release, install, support, SLO, HA, provenance,
   durable verification-evidence, launch infrastructure, storage-risk, and
   branding tickets: `EGA-01` through `EGA-14`, plus `EGA-31` through
   `EGA-34`.
2. Close P1 procurement, compliance, security response, operator UX, tenant
   lifecycle, cloud examples, release-artifact drills, capacity certification,
   and package compatibility strategy: `EGA-15` through `EGA-25`.
3. Close P2 adoption accelerators after the first production candidate is
   stable: `EGA-26` through `EGA-29`.
4. Keep `EGA-30` deferred unless the enterprise support envelope expands to
   multi-region disaster recovery.

## Launch Gate

Do not call Kublai enterprise GA until all P0 tickets are closed and the
following evidence exists for a release candidate tag:

- passing CI and integration tests
- passing enterprise verification battery
- consolidated enterprise verification summary artifact
- signed release artifacts, signed images, signed chart, and SBOMs
- verified release provenance from downloaded artifacts
- Helm install and upgrade certification result
- production preflight result
- HA deployment validation report
- backup/restore, reliability, and upgrade drill reports
- security whitepaper and support envelope
- administrator handbook and cutover guide
- SLO/SLI and alert routing guide
- support bundle workflow evidence

P1 tickets should be closed before the first paid production customer unless a
specific exception is accepted in the GA sign-off board.
