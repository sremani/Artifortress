# Mutation Track Report

Generated at: 2026-02-11T17:16:24Z

## Inputs

- test project: `tests/Artifortress.Mutation.Tests/Artifortress.Mutation.Tests.fsproj`
- project under mutation: `src/Artifortress.Domain/Artifortress.Domain.fsproj`
- mutate scope: `src/Artifortress.Domain/Library.fs{8..102}`
- mutation level: `Basic`
- concurrency: `1`
- baseline tests executed: `True`
- stryker cli: `dotnet tool run dotnet-stryker`

## Outcome

- status: `blocked_fsharp_not_supported`
- exit code: `134`
- summary: Known blocker: Stryker does not yet support F# mutation in this execution path.
- started at: `2026-02-11T17:15:58Z`
- finished at: `2026-02-11T17:16:24Z`
- log: `/tmp/artifortress-mutation-track.log`
- output path: `artifacts/mutation/mut01-domain`

## Next Action

- Continue Track B engine work (analyzer/mutator implementation in fork/upstream).
