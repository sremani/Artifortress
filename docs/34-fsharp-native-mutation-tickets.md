# F# Native Mutation Finish Plan

Last updated: 2026-02-11

Status key:
- `todo`: not started
- `in_progress`: active work
- `done`: implemented and validated
- `blocked`: external dependency blocks completion
- `partial`: implementation is complete, but exit criteria depend on elapsed burn-in time

## Why This Plan

We are intentionally not depending on Stryker fork maturity for side-quest completion.
Primary path is now native F# mutation execution in this repository.

## Ticket Board

| Ticket | Title | Status | Notes |
|---|---|---|---|
| MUTN-01 | Native runtime command scaffold (`run-fsharp-native`) | done | Added wrapper command and artifact outputs. |
| MUTN-02 | Runtime execution lane script + Make target | done | Added `scripts/mutation-fsharp-native-run.sh` and `make mutation-fsharp-native`. |
| MUTN-03 | Deterministic status model (`killed`, `survived`, `compile_error`, `infrastructure_error`) | done | Added classification and JSON/markdown reporting. |
| MUTN-04 | Safety guards for scratch workspace mutation runs | done | Reused artifact-root and workspace-containment guards. |
| MUTN-05 | CI native mutation lane (non-blocking) | done | Added `mutation-native` job in `.github/workflows/mutation-track.yml` with artifact upload. |
| MUTN-06 | Expand safe rewrite set beyond initial lexical whitelist | done | Enabled safe `=` expression mutations in boolean contexts; latest native run discovered `7` and selected `6` mutants with `0` compile errors. |
| MUTN-07 | Mutation score policy + trend report | done | Added score script/report with threshold policy (`MIN_MUTATION_SCORE`) and CI artifacts. |
| MUTN-08 | Promote native lane to merge gate | partial | Enforcement toggle and thresholds are wired via GitHub variables; promotion is intentionally pending completion of the 7-run burn-in window. |
| MUTN-09 | Native score history retention across CI runs | done | Added trend script and cache-backed history CSV persistence (`mutation-native-score-history.csv`). |
| MUTN-10 | Burn-in readiness evaluator + artifact | done | Added `mutation-fsharp-native-burnin` script/target and CI artifact that computes readiness from history streak rules. |
| MUTN-11 | Enforce-time burn-in interlock | done | Burn-in checker now fails promotion attempts when `MUTATION_NATIVE_ENFORCE=true` but readiness streak is not yet satisfied. |

## Current Acceptance Bar

1. Native runtime command works locally with deterministic artifacts.
2. At least one mutant receives runtime-tested status (`killed` or `survived`).
3. No destructive-path risk in scratch workspace handling.

## Current Evidence

1. `MAX_MUTANTS=6 make mutation-fsharp-native`:
   - selected: `6`
   - killed: `5`
   - survived: `1`
   - compile errors: `0`
   - infrastructure errors: `0`
2. `MIN_MUTATION_SCORE=40 make mutation-fsharp-native-score`:
   - score: `83.33`
   - threshold met: `true`
3. `make mutation-fsharp-native-trend`:
   - appends score row to `artifacts/mutation/mutation-native-score-history.csv`
   - generates `docs/reports/mutation-native-score-history-latest.md`
4. `REQUIRED_STREAK=7 MIN_MUTATION_SCORE=40 make mutation-fsharp-native-burnin`:
   - computes passing streak over retained history,
   - generates `docs/reports/mutation-native-burnin-latest.md`,
   - marks readiness as pending until streak criterion is met.

## Next Steps

1. Keep `MUTN-08` in partial state until burn-in report shows ready:
   - current gate signal: `docs/reports/mutation-native-burnin-latest.md`
   - required: 7-run passing streak with zero compile/infra errors.
2. Promote to blocking mode once ready:
   - set `MUTATION_NATIVE_ENFORCE=true`,
   - keep `MUTATION_NATIVE_MAX_MUTANTS` and `MUTATION_NATIVE_MIN_SCORE` tuned for CI budget.
