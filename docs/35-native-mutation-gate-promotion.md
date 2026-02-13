# Native Mutation Gate Promotion Guide

Last updated: 2026-02-11

## Goal

Promote native F# mutation lane from non-blocking visibility mode to blocking merge gate mode.

## Current Control Knobs

The `mutation-native` job in `.github/workflows/mutation-track.yml` reads these GitHub repository variables:

- `MUTATION_NATIVE_ENFORCE`
  - `true`: job is blocking (`continue-on-error: false`)
  - any other value: job is non-blocking
- `MUTATION_NATIVE_MAX_MUTANTS`
  - defaults to `6` when unset
- `MUTATION_NATIVE_MIN_SCORE`
  - defaults to `40` when unset
- `MUTATION_NATIVE_REQUIRED_STREAK`
  - defaults to `7` when unset

## Promotion Criteria

1. Stability window
- 7 consecutive scheduled runs with:
  - `infrastructureErrorCount = 0`
  - `compileErrorCount = 0`

2. Quality floor
- Mutation score meets or exceeds configured threshold for all 7 runs.

3. Runtime budget
- Job duration remains within agreed CI budget for 95th percentile runs.

4. Burn-in signal
- `docs/reports/mutation-native-burnin-latest.md` reports:
  - `burn-in ready: true`
  - `passing streak >= required streak`

## Rollout Steps

1. Burn-in mode (current)
- Keep `MUTATION_NATIVE_ENFORCE` unset.
- Tune `MUTATION_NATIVE_MAX_MUTANTS` and `MUTATION_NATIVE_MIN_SCORE` for signal-to-cost balance.

2. Soft enforcement
- Set `MUTATION_NATIVE_MIN_SCORE` to target floor (example `50`).
- Keep non-blocking while observing trend and burn-in readiness report.

3. Hard enforcement
- Set `MUTATION_NATIVE_ENFORCE=true`.
- Native lane becomes blocking for merges.
- Burn-in interlock is automatically enforced in CI:
  - promotion fails if required streak is not yet ready.

## Rollback Plan

If flaky behavior appears after promotion:

1. Set `MUTATION_NATIVE_ENFORCE` to empty/unset.
2. Lower `MUTATION_NATIVE_MAX_MUTANTS` to reduce runtime pressure.
3. Triage failures from:
- `docs/reports/mutation-native-fsharp-latest.md`
- `docs/reports/mutation-native-score-latest.md`
- `docs/reports/mutation-native-burnin-latest.md`
- `artifacts/ci/mutation-native-fsharp-summary.txt`
- `artifacts/ci/mutation-native-score-summary.txt`
- `artifacts/ci/mutation-native-burnin-summary.txt`
