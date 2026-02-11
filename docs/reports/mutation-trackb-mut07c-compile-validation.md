# MUT-07c F# Compile Validation Report

Generated at: 2026-02-11T20:33:14Z

## Inputs

- project path: `src/Artifortress.Domain/Artifortress.Domain.fsproj`
- source file path: `src/Artifortress.Domain/Library.fs`
- max mutants: `9`
- scratch root: `artifacts/mutation/mut07c-compile`

## Summary

- discovered candidates: `5`
- selected mutants: `5`
- compile successes: `5`
- compile failures: `0`

## Sample Results

| # | Location | Family | Mutation | Compile |
|---|---|---|---|---|
| 1 | L83:C34 | boolean | `|| -> &&` | pass |
| 2 | L83:C29 | comparison | `<> -> =` | pass |
| 3 | L83:C73 | comparison | `<> -> =` | pass |
| 4 | L97:C49 | boolean | `|| -> &&` | pass |
| 5 | L98:C23 | boolean | `&& -> ||` | pass |

All sampled mutants compiled successfully.
