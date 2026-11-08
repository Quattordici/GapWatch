# GapWatch: instructions for Claude

GapWatch detects breaking changes between two versions of an HTTP API contract. Read `README.md` first for scope, rules and limitations.

## Stack and conventions

- C#, .NET 10, nullable enabled, warnings as errors, central package management (`Directory.Packages.props`).
- Tests: TUnit (`TUnit.Core` + `TUnit.Engine`, on Microsoft Testing Platform), `TUnit.Mocks` for test doubles, Shouldly for assertions. Do not add the `TUnit` meta-package or `TUnit.Assertions`; Shouldly is the only assertion library. TUnit runs tests in parallel by default, so tests must not share mutable state (use `[NotInParallel]` only when unavoidable). Keep assertions simple (`ShouldBe`, `ShouldContain`, `ShouldBeEmpty`, `ShouldHaveSingleItem`, `Should.Throw`); compare collections as joined strings.
- Prefer small, pure, well-tested core libraries. `GapWatch.Core` has no I/O and no package dependencies; keep it that way.

## Architecture rules

- Every source format compiles into the normalized `ApiContract`. Rules only see that model. Never let reader-specific types leak into `GapWatch.Core`.
- The C# reader (`CSharpContractReader` in `GapWatch.Roslyn`, `--input csharp`) must produce the same operation and location strings as the OpenAPI readers, so suppressions and baselines carry over. Document every C#-to-contract mapping in `docs/csharp-input.md`.
- Configuration (severity overrides, disabled rules, justified suppressions) is applied **after** comparison by `CompatibilityPolicy.Apply`. Rules never read configuration. Reading `gapwatch.json` lives in `GapWatch.Cli` (`ConfigFile`).
- Comparison is **direction-aware** (`Direction.Request` vs `Direction.Response`). Every new rule must state which direction it applies to and why.
- Rule ids (`GWnnn`) are public and permanent: never renumber, reuse or change the meaning of one. Add new ids at the end of their group, and append the rule to `ReleasedRules` in `PublicContractTests`; never edit or remove a line there.
- `GapWatchCli.Run` stays free of `Console` so it remains testable. Exit codes (0, 1, 2) are a public contract.
- From 1.0, `docs/compatibility.md` defines what is stable and which release may change it. Check every change against it, and say when one needs a major release.

## When adding or changing a rule

Provide: id, name, default severity, direction, a before/after example, why it breaks (or does not break) a consumer, and tests for **both** directions (the safe direction matters as much as the breaking one). Update `docs/rules.md` (`gapwatch rules --format markdown`) and the golden files if output changes (`GAPWATCH_UPDATE_GOLDEN=1`, then review the diff).

## Working style

- Challenge scope creep and say which milestone a feature belongs to (see the roadmap in the README).
- Verify OpenAPI semantics against the specification rather than memory; call out places where tools disagree.
- Do not produce PowerShell scripts or pipeline definitions unless explicitly asked.
