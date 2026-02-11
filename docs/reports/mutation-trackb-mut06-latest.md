# Mutation Track Report

Generated at: 2026-02-11T19:57:11Z

## Inputs

- test project: `tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj`
- project under mutation: `src/Artifortress.Domain/Artifortress.Domain.fsproj`
- mutate scope: `src/Artifortress.Domain/Library.fs`
- mutation level: `Basic`
- concurrency: `1`
- baseline tests executed: `True`
- stryker cli: `dotnet tool run dotnet-stryker`

## Outcome

- status: `quarantined_compile_errors`
- exit code: `0`
- summary: Mutants were generated and quarantined as compile errors pending runtime activation.
- started at: `2026-02-11T19:57:11Z`
- finished at: `2026-02-11T19:57:11Z`
- log: `/tmp/artifortress-mutation-trackb-mut06.log`
- output path: `artifacts/mutation/mut01-domain`

## Extracted Metrics

- candidates discovered: `71`
- mapped spans: `71`
- planned mutants: `71`
- created mutants: `71`
- quarantined compile errors: `71`

## Next Action

- Continue post-plan runtime activation work: replace compile-error quarantine with executable tested mutant activation.
