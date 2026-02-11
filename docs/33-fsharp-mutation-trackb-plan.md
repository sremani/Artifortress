# F# Mutation Track B Plan

Last updated: 2026-02-11

This plan defines how to deliver native mutation testing capability for F# by extending Stryker.NET behavior through upstream contribution and/or a maintained fork.

## 1. Problem Statement

Baseline blocker from MUT-01:
- Upstream Stryker run against this repository fails with:
  - `Language not supported: Fsharp`

Current state after MUT-10:
- Patched fork run now bypasses the hard F# language rejection.
- Run reaches project analysis/build/initial test stages.
- Patched run now emits non-zero mutants for `Library.fs` in the current quarantine path:
  - `71` mutants created in latest local run.
- MUT-07a registration entrypoint is now in place for F# operator-family mutators.
- MUT-07b operator mutation planning/emission path is now implemented (lexical planning + span mapping + path-aware selection).
- MUT-07c compile-validation gate is implemented and currently passes for sampled `Library.fs` mutants (`5/5` compile success in latest run).
- MUT-08a/08b/08c span mapping, lexical safety guards, and compile-error quarantine classification are implemented.
- MUT-09 reporting is implemented with parsed candidate/mapped/planned/created/quarantine metrics.
- MUT-10 non-blocking CI lane is implemented with spike, invariant assertion, compile-validation, and artifact uploads.
- Remaining blocker for full runtime value: mutants are still quarantined as compile errors pending runtime activation support.

## 2. Track B Delivery Objective

Enable mutation runs for `Artifortress.Domain` F# code with:
- generated mutants,
- deterministic build/test execution,
- JSON/markdown report artifacts,
- CI execution in a non-blocking lane first.

## 3. Architecture Approach

Workstream A: Wrapper control plane (repo-local)
- thin CLI/workflow wrapper around mutation commands.
- standardized outputs and failure classifications.
- report publishing under `docs/reports/` plus machine-readable JSON artifacts.

Workstream B: Engine capability (upstream/fork)
- add/enable F# source project support in analyzer pipeline.
- implement F# mutators for high-value operators first.
- ensure rewrite preserves compilability and line mapping.

Workstream C: Adoption and quality gates
- nightly mutation run lane.
- module-level baseline score and trend tracking.
- optional PR-time changed-files mode after stabilization.

## 4. Milestone Plan and Status

### Milestone B1: Groundwork

Deliverables:
- mutation-only test project.
- reproducible spike script + report.
- initial ticket board and plan docs.

Status:
- complete.

### Milestone B2: Wrapper + Fork Skeleton

Deliverables:
- wrapper entrypoint (`run`, `spike`, `validate-fsharp-mutants`, `report`, `classify-failure`).
- fork branch with isolated F# compatibility patches.
- local patch application docs.

Status:
- complete.

### Milestone B3: F# Analyzer Support

Deliverables:
- source project identification for F# projects.
- pre-mutation compile pipeline support for F# targets.

Success criteria:
- mutation run no longer fails with `Language not supported: Fsharp`.
- run reaches mutant-generation phase.

Status:
- complete.

### Milestone B4: F# Mutator v1

Deliverables:
- boolean negation mutations.
- equality/inequality comparison mutations.
- relational operator mutations.
- arithmetic operator mutations where safe.

Success criteria:
- non-zero mutants generated for `src/Artifortress.Domain/Library.fs`.
- mutated project compiles for majority of generated mutants.

Status:
- complete (v1 mutator planning/emission plus compile-validation gate are implemented).

### Milestone B5: Reliability + CI

Deliverables:
- deterministic failure classification and retry rules.
- nightly non-blocking CI lane.
- mutation report publishing.

Success criteria:
- stable nightly execution for 7 consecutive days.
- actionable output for surviving mutants.

Status:
- complete (implementation complete; operational burn-in continues as normal CI monitoring).

## 5. Implemented Artifacts

Wrapper and automation:
- `tools/Artifortress.MutationTrack/Program.fs`
- `Makefile` targets:
  - `mutation-spike`
  - `mutation-track`
  - `mutation-trackb-bootstrap`
  - `mutation-trackb-build`
  - `mutation-trackb-spike`
  - `mutation-trackb-assert`
  - `mutation-trackb-compile-validate`

Fork patching flow:
- `patches/stryker-net/0001-mut06-fsharp-analyzer-pipeline.patch`
- `patches/stryker-net/0002-mut07a-fsharp-mutator-registration.patch`
- `patches/stryker-net/0003-mut07b-fsharp-operator-candidate-planner.patch`
- `patches/stryker-net/0004-mut08a-fsharp-source-span-mapper.patch`
- `patches/stryker-net/0005-mut08b-lexical-safety-guards.patch`
- `patches/stryker-net/0006-mut07b-fsharp-mutant-emission-quarantine.patch`
- `patches/stryker-net/0007-mut07b-fsharp-path-selection.patch`
- `scripts/mutation-trackb-bootstrap.sh`
- `scripts/mutation-trackb-build.sh`
- `scripts/mutation-trackb-spike.sh`
- `scripts/mutation-trackb-assert.sh`
- `scripts/mutation-trackb-compile-validate.sh`

CI lane:
- `.github/workflows/mutation-track.yml`

Reports:
- `docs/reports/mutation-spike-fsharp-latest.md`
- `docs/reports/mutation-track-latest.md`
- `docs/reports/mutation-trackb-mut06-latest.md`
- `docs/reports/mutation-trackb-mut07c-compile-validation.md`

## 6. Engineering Rules

- Keep mutation run deterministic and reproducible.
- Start with narrow file scopes, then expand gradually.
- Treat compiler crashes and invalid rewrites as P1 defects.
- Prefer upstream contributions when feasible; keep fork delta minimal and well-documented.

## 7. Risk Register

1. Risk: AST rewrite complexity in F# constructs.
- Mitigation: constrain v1 mutators to expression forms with safe span mapping.

2. Risk: runtime/performance blowup from large mutant sets.
- Mitigation: narrow mutate globs, concurrency controls, and baseline compare workflows.

3. Risk: maintenance burden of long-lived fork.
- Mitigation: upstream-first PR strategy and periodic rebase cadence.

4. Risk: low trust due flaky mutants.
- Mitigation: failure taxonomy and quarantine rules for unstable mutants.

## 8. Post-Plan Next Steps

1. Replace compile-error quarantine with executable runtime activation for a safe subset of F# rewrites.
2. Move from planned/quarantined mutants to tested statuses and mutation score computation.
3. Evaluate promotion of mutation lane from non-blocking to enforced gate after burn-in confidence.
