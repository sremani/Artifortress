# Native F# Mutation Burn-in Readiness

Generated at: 2026-02-11T21:49:34Z

## Inputs

- history csv: artifacts/mutation/mutation-native-score-history.csv
- required passing streak: 7
- minimum score floor: 40

## Current Status

- total history rows: 4
- current passing streak: 4
- latest run timestamp: 2026-02-11T21:49:25Z
- burn-in ready: false
- reason: insufficient_history

Readiness rule for each run in streak:
- compile_error_count == 0
- infrastructure_error_count == 0
- threshold_met == true
- score_percent >= MIN_MUTATION_SCORE

## Recent Runs (latest 20)

| timestamp | selected | tested | killed | survived | compile_error | infra_error | score | min_score | threshold_met |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 2026-02-11T21:41:21Z | 6 | 6 | 5 | 1 | 0 | 0 | 83.33 | 40 | true |
| 2026-02-11T21:42:25Z | 6 | 6 | 5 | 1 | 0 | 0 | 83.33 | 40 | true |
| 2026-02-11T21:42:43Z | 6 | 6 | 5 | 1 | 0 | 0 | 83.33 | 40 | true |
| 2026-02-11T21:49:25Z | 6 | 6 | 5 | 1 | 0 | 0 | 83.33 | 40 | true |
