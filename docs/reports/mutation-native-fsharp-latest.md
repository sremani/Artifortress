# Native F# Mutation Runtime Report

Generated at: 2026-02-11T21:49:25Z

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
| 1 | L83:C34 | `|| -> &&` | survived | 10677 |
| 2 | L83:C29 | `<> -> =` | killed | 10721 |
| 3 | L83:C73 | `<> -> =` | killed | 13736 |
| 4 | L97:C43 | `= -> <>` | killed | 18031 |
| 5 | L97:C49 | `|| -> &&` | killed | 9362 |
| 6 | L97:C66 | `= -> <>` | killed | 16305 |
