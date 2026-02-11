# F# Mutation Track Tickets

Last updated: 2026-02-11

Status key:
- `todo`: not started
- `in_progress`: active work
- `done`: implemented and validated
- `blocked`: cannot proceed until dependency gap is resolved

## Ticket Board

| Ticket | Title | Depends On | Status | Notes |
|---|---|---|---|---|
| MUT-01 | Feasibility spike for Stryker on F# projects | none | done | Confirmed upstream blocker (`Language not supported: Fsharp`). |
| MUT-02 | Mutation-only test project isolation (no integration tests) | MUT-01 | done | Added `tests/Artifortress.Mutation.Tests` and validated clean baseline test run. |
| MUT-03 | Reproducible spike script + report artifact | MUT-02 | done | Added `scripts/mutation-fsharp-spike.sh` and report artifact flow. |
| MUT-04 | Upstream/fork architecture and backlog definition | MUT-01 | done | Added Track B strategy and milestone plan docs. |
| MUT-05 | Local wrapper CLI scaffold for Track B workflows | MUT-04 | done | Added `tools/Artifortress.MutationTrack` with `spike`, `run`, `validate-fsharp-mutants`, `report`, `classify-failure`. |
| MUT-06 | Analyzer pipeline patching for F# source project support | MUT-04 | done | Added reproducible patch + bootstrap/build/spike scripts; patched run reaches analysis/build/test and mutant-generation phases. |
| MUT-07 | F# mutator set v1 (boolean/comparison/arithmetic) | MUT-06 | done | MUT-07a, MUT-07b, and MUT-07c are implemented and validated: patched run emits `71` planned mutants and compile-validation gate passes on sampled `Library.fs` mutants. |
| MUT-08 | F# AST span mapping and source rewrite safety checks | MUT-06 | done | MUT-08a/08b/08c are implemented: span mapping + lexical safety guards + deterministic compile-error quarantine classification (`quarantined_compile_errors`). |
| MUT-09 | Baseline reporter integration + mutant run orchestration | MUT-07, MUT-08 | done | Wrapper writes markdown + JSON artifacts, parses candidate/mapped/planned/created/quarantine metrics, and reports deterministic classifications. |
| MUT-10 | CI mutation lane (non-blocking) + runbook | MUT-09 | done | Nightly/manual non-blocking workflow now runs spike + invariants assertion + MUT-07c compile-validation and uploads all generated artifacts. |

## MUT-05 Completed Scope

Implemented:
- `tools/Artifortress.MutationTrack/Program.fs`
  - Commands: `spike`, `run`, `validate-fsharp-mutants`, `report`, `classify-failure`.
  - Deterministic failure classification.
  - Optional `--stryker-cli` override for patched fork runs.
  - Markdown and JSON report artifact generation.
- `Makefile`
  - `mutation-spike`
  - `mutation-track`

Validation evidence:
- `dotnet build tools/Artifortress.MutationTrack/Artifortress.MutationTrack.fsproj`
- `make mutation-spike`
- `make mutation-track`

## MUT-06 Completed Scope

Implemented:
- Patch artifact:
  - `patches/stryker-net/0001-mut06-fsharp-analyzer-pipeline.patch`
- Reproducible fork workflow scripts:
  - `scripts/mutation-trackb-bootstrap.sh`
  - `scripts/mutation-trackb-build.sh`
  - `scripts/mutation-trackb-spike.sh`
- `Makefile` targets:
  - `mutation-trackb-bootstrap`
  - `mutation-trackb-build`
  - `mutation-trackb-spike`

Validation evidence:
- `make mutation-trackb-spike`
- Latest report:
  - `docs/reports/mutation-trackb-mut06-latest.md`
- Latest result JSON:
  - `artifacts/mutation/mut06-trackb-latest.json`

Observed MUT-06 outcome:
- Patched run no longer throws `Language not supported: Fsharp`.
- Run reaches project identification, build, initial test, and mutant-generation phases.
- Follow-on tickets (MUT-07+): experimental F# mutant planning and quarantine flow now active.

## MUT-07 Completed Scope

Implemented:
- Patch artifact:
  - `patches/stryker-net/0002-mut07a-fsharp-mutator-registration.patch`
  - `patches/stryker-net/0003-mut07b-fsharp-operator-candidate-planner.patch`
  - `patches/stryker-net/0006-mut07b-fsharp-mutant-emission-quarantine.patch`
  - `patches/stryker-net/0007-mut07b-fsharp-path-selection.patch`
- MUT-07a:
  - Added language-aware mutator profile registry in fork (`MutatorSetRegistry`).
  - Added explicit F# operator-family mutator registration entrypoint.
- MUT-07b:
  - Added lexical F# operator candidate planner (`FsharpOperatorMutationPlanner`).
  - Added F# operator mutant emitter (`FsharpOperatorMutantEmitter`) with boolean/comparison/arithmetic replacement planning.
  - Added fallback `.fs` source discovery scan when Buildalyzer source file list is empty for F# targets.
  - Added mutate-glob path matching against run-root relative file paths.
  - Added fork unit-test coverage for emitter behavior.
  - Current observed output: `71` operator candidates, `71` mapped spans, and `71` planned mutants emitted.
- MUT-07c:
  - Added wrapper command `validate-fsharp-mutants` to compile-validate sampled F# operator mutants against `Library.fs`.
  - Added script `scripts/mutation-trackb-compile-validate.sh` and Make target `mutation-trackb-compile-validate`.
  - Added CI assertion artifacts:
    - `docs/reports/mutation-trackb-mut07c-compile-validation.md`
    - `artifacts/mutation/mut07c-compile-validation.json`
    - `artifacts/ci/mutation-trackb-compile-validate-summary.txt`
    - `artifacts/ci/mutation-trackb-compile-validate-metrics.json`
  - Latest local validation result:
    - discovered candidates: `5`
    - selected mutants: `5`
    - compile successes: `5`
    - compile failures: `0`

## MUT-08 Completed Scope

Implemented:
- Patch artifact:
  - `patches/stryker-net/0004-mut08a-fsharp-source-span-mapper.patch`
  - `patches/stryker-net/0005-mut08b-lexical-safety-guards.patch`
- MUT-08a:
  - Added F# source span mapper (`FsharpSourceSpanMapper`) to convert candidate line/column coordinates to absolute edit spans.
  - Wired mapped-span metrics into F# mutation run logging path.
  - Added unit-test coverage in fork for span mapping behavior.
- MUT-08b:
  - Added lexical safety guards to skip comments/strings and invalid token mappings before mutation planning.
- MUT-08c:
  - Emitted F# planned mutants are currently quarantined as `CompileError` with deterministic reason:
    - `F# mutant is quarantined pending runtime activation support.`
  - Wrapper classification now exposes `quarantined_compile_errors`.

Validation evidence:
- `make mutation-trackb-spike`
  - confirms `0001` through `0007` patch apply order.
  - logs F# mutator registration profile selection.
  - logs F# operator candidate/mapped-span/planned-mutant metrics.
  - latest run result:
    - `71 mutants created`
    - wrapper classification: `quarantined_compile_errors`

## MUT-07 / MUT-08 Subtasks

| Subtask | Parent | Description | Status |
|---|---|---|---|
| MUT-07a | MUT-07 | Add F# mutator registration path in fork (operator set entrypoint). | done |
| MUT-07b | MUT-07 | Implement v1 operator mutators (boolean/comparison/arithmetic) over F# syntax constructs. | done |
| MUT-07c | MUT-07 | Add compiler-validated mutant generation tests for `Library.fs` fixture scope. | done |
| MUT-08a | MUT-08 | Implement F# span mapping from syntax nodes to stable text edits. | done |
| MUT-08b | MUT-08 | Add rewrite safety checks (comments/strings/invalid span guards). | done |
| MUT-08c | MUT-08 | Add compile-fail quarantine classification for invalid rewrites. | done |

## MUT-09 / MUT-10 Completed Scope

MUT-09 implemented:
- JSON + markdown reporting in wrapper flow.
- Classification includes `no_mutants_generated` for deterministic gate behavior.
- Classification now includes `quarantined_compile_errors`.
- Extracted metrics include candidate/mapped/planned/created/quarantined counts.

MUT-10 implemented:
- `.github/workflows/mutation-track.yml` (nightly + manual, non-blocking lane).
- Executes:
  - `make mutation-trackb-spike`
  - `RUN_SPIKE=false make mutation-trackb-assert`
  - `RUN_SPIKE=false make mutation-trackb-compile-validate`
- Uploads report, JSON, log, assertion, and compile-validation artifacts for triage.

## Post-Plan Backlog

1. Replace compile-error quarantine with executable runtime activation for a safe subset of F# rewrites.
2. Move from planned/quarantined mutants to tested mutant statuses and score computation.
3. Convert non-blocking mutation lane to an enforced quality gate after operational burn-in.
