# Native F# Mutation Runtime Report

Generated at: 2026-02-11T21:02:11Z

## Inputs

- source file path: `src/Artifortress.Domain/Library.fs`
- test project path: `tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj`
- max mutants: `6`
- scratch root: `artifacts/mutation/native-fsharp-runtime`

## Summary

- discovered candidates: `7`
- selected mutants: `6`
- killed: `5`
- survived: `1`
- compile errors: `0`
- infrastructure errors: `0`

## Mutant Outcomes

| # | Location | Mutation | Status | Duration (ms) |
|---|---|---|---|---|
| 1 | L83:C34 | `|| -> &&` | survived | 11293 |
| 2 | L83:C29 | `<> -> =` | killed | 11754 |
| 3 | L83:C73 | `<> -> =` | killed | 13727 |
| 4 | L97:C43 | `= -> <>` | killed | 13373 |
| 5 | L97:C49 | `|| -> &&` | killed | 10577 |
| 6 | L97:C66 | `= -> <>` | killed | 12330 |
