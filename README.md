# GapWatch

GapWatch finds **breaking changes between two versions of an HTTP API contract** before they reach your consumers. It compares a baseline OpenAPI document with a proposed one and explains every consumer-visible difference, with a rule id, a precise location and a severity.

> **Status: v0.1, pre-release.** The core engine, rule set, reports and CLI work and are tested. See [Limitations](#limitations-in-v01).

## Why another API diff tool

- **Direction-aware.** The same change is breaking or safe depending on who produces the value. A new enum value in a *response* can break a client's `switch`; in a *request* it is harmless. Removing an enum value is the opposite. GapWatch models this explicitly instead of reporting "something changed".
- **Explainable.** Every finding carries a stable rule id (`GW025`), the operation, the location inside it (`response 200 application/json: $[].name`) and a complete sentence.
- **Built for .NET teams.** Understands `readOnly`/`writeOnly`, nullable handling, `int32`→`int64` widening, `operationId` changes that break generated clients, path-parameter renames, and security changes.
- **Dependency-free core.** The engine and the JSON reader use only the .NET base class library.

## Quick start

```
dotnet build
dotnet run --project src/GapWatch.Cli -- compare before.json after.json
```

Install as a global tool once packed:

```
dotnet pack src/GapWatch.Cli -c Release
dotnet tool install --global --add-source src/GapWatch.Cli/bin/Release GapWatch
gapwatch compare before.json after.json
```

(`GapWatch` is a placeholder package id until the NuGet name is confirmed free.)

## Usage

```
gapwatch compare <base> <head> [--format console|markdown|sarif|junit] [--output <file>] [--fail-on breaking|warning|none] [--config <file>]
gapwatch rules [--format console|markdown]
gapwatch --version | --help
```

- `<base>` is the contract consumers rely on today; `<head>` is the proposed change.
- `--format markdown` produces a report for pull request comments. It starts with a hidden marker (`<!-- gapwatch-report -->`) so a pipeline can find and update one comment instead of posting a new one each run.
- `--format sarif` writes a SARIF 2.1.0 log for code-scanning views (GitHub code scanning, the Azure DevOps SARIF viewer, IDE SARIF viewers). See [Output formats](#output-formats).
- `--format junit` writes JUnit XML for CI test tabs (Azure DevOps, Jenkins, GitLab).
- `--output` writes the report to a file and prints a one-line summary.
- `--config` reads a `gapwatch.json` with severity overrides and suppressions (see [Configuration](#configuration)).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | No finding at or above the `--fail-on` threshold (default threshold: `breaking`). |
| 1 | At least one finding at or above the threshold. |
| 2 | Usage error or unreadable input. |

Exit codes are part of the public contract, so build pipelines can rely on them.

## Example

```
GET /pets   response 200 application/json: $[].id
  GW020  Type changed from 'integer' to 'string'.
GET /pets   parameter 'region' (query)
  GW010  Required parameter 'region' (query) was added.
POST /pets  request body (application/json): $.ownerId
  GW027  New required request property 'ownerId' was added.
```

A complete before/after sample lives in `tests/GapWatch.Tests/Fixtures`, with the full reports in `tests/GapWatch.Tests/Golden`.

## Output formats

| Format | For | Notes |
|---|---|---|
| `console` | Build logs | Grouped by severity. |
| `markdown` | Pull request comments | Starts with `<!-- gapwatch-report -->` so a pipeline can update one comment. |
| `sarif` | Code-scanning dashboards | SARIF 2.1.0, validated against the OASIS schema in development. Levels: Breaking `error`, Warning `warning`, Info `note`. Suppressed findings are kept with `suppressions[].kind = "external"` and their justification. `partialFingerprints` hash rule, operation and location, so a finding keeps its identity when its wording changes. |
| `junit` | CI test tabs | One test case per finding, grouped by operation. Findings at or above `--fail-on` are failures, so the XML agrees with the exit code; suppressed findings are skipped with their justification. |

SARIF results point at the head document at line 1, plus a logical location (`GET /pets > response 200 application/json: $[].id`). Readers do not record source positions yet; real line numbers arrive with source mapping in v0.3.

### Changes in shared schemas

A change inside a component schema (`#/components/schemas/Pet`) affects every operation that uses it. Console and Markdown reports show it once, at its cause, with the operations it affects:

```
  [GW029] Pet $.name (response), affects 3 operations
          Response property 'name' is no longer guaranteed to be present.
          - GET /pets       response 200 application/json: $[].name
          - GET /pets/{id}  response 200 application/json: $.name
          - POST /pets      response 201 application/json: $.name
```

Request and response uses of the same schema stay separate, because the same change is judged differently in each direction. Counts, `--fail-on`, suppressions, SARIF and JUnit still work per operation (SARIF results carry `component` and `componentPath` properties). A keyword next to a nested `$ref` (for example `nullable` on `owner`) is attributed to the schema that holds the property (`Pet $.owner`), not to the referenced one.

## Configuration

A `gapwatch.json` passed with `--config` changes how findings are judged without touching the rules:

```json
{
  "rules": { "GW004": "breaking", "GW003": "off" },
  "suppressions": [
    {
      "rule": "GW025",
      "operation": "GET /pets",
      "location": "response 200 application/json: $[].nickname",
      "justification": "Never populated in production; removal agreed with all consumers."
    }
  ]
}
```

Every suppression needs a justification. Suppressed findings leave the exit code but stay visible in a separate section of the report, and suppressions that no longer match anything are listed so they can be cleaned up. Full reference: [docs/configuration.md](docs/configuration.md).

## Rules

28 rules across operations, parameters, schemas, bodies, responses and security. See [docs/rules.md](docs/rules.md), or run `gapwatch rules`. Rule ids are stable: they never change meaning and are never reused.

### How direction changes the verdict

| Change | In a request (consumers send it) | In a response (consumers read it) |
|---|---|---|
| New required property | Breaking | Safe |
| Property removed | Warning | Breaking if it was required |
| Optional becomes required | Breaking | Safe |
| Required becomes optional | Safe | Breaking |
| Enum value added | Safe | Warning |
| Enum value removed | Breaking | Safe |
| `integer` widened to `number` | Safe | Breaking |
| `int32` widened to `int64` | Safe | Breaking |
| Non-nullable becomes nullable | Safe | Breaking |

## Architecture

```
src/
  GapWatch.Core        Contract model, rule catalog, comparison engine, policy (overrides, suppressions). No I/O, no dependencies.
  GapWatch.OpenApi     OpenAPI 3.x JSON reader (System.Text.Json) behind IContractReader.
  GapWatch.Reporting   Console, Markdown, SARIF and JUnit renderers, rule list. BCL only.
  GapWatch.Cli         The `gapwatch` dotnet tool and the gapwatch.json reader. GapWatchCli.Run is console-free and unit tested.
tests/
  GapWatch.Tests       TUnit + TUnit.Mocks + Shouldly. One test per rule and direction, reader tests, golden reports, CLI tests.
```

Every source format compiles into one normalized `ApiContract`; rules only ever see that model. That is what lets a Roslyn-based reader (DTOs and endpoints from source) be added later without touching a single rule.

## Running the tests

`dotnet test` runs on the Microsoft Testing Platform (configured in `global.json`, requires the .NET 10 SDK). The tests use TUnit (`TUnit.Core` + `TUnit.Engine`, without `TUnit.Assertions`), `TUnit.Mocks` for test doubles and Shouldly for assertions. Package versions are pinned in `Directory.Packages.props`.

To regenerate the golden reports after an intentional output change, run the tests with `GAPWATCH_UPDATE_GOLDEN=1` and review the diff.

## Limitations in v0.1

- **JSON only.** OpenAPI documents must be JSON (what ASP.NET Core and Swashbuckle emit). YAML needs the `Microsoft.OpenApi` reader, planned behind the same `IContractReader`.
- **Local references only.** External `$ref`s are rejected with a clear message; bundle the document first. Swagger 2.0 is not supported.
- **No value constraints yet.** `minLength`, `maxLength`, `pattern`, `minimum`, `maximum`, `additionalProperties` and `discriminator` are not compared.
- **Two files in, no git refs.** Obtaining the base spec from a commit or branch is not built yet.
- **Explicit configuration only.** `gapwatch.json` must be passed with `--config`; it is not discovered automatically.
- **No source positions.** SARIF results point at line 1 of the head document; mapping to real lines comes with v0.3.

## Roadmap

| Version | Scope |
|---|---|
| v0.1 | OpenAPI JSON diff, 28 rules, console and Markdown reports (this release) |
| v0.2 | `gapwatch.json` severity overrides and suppressions that require a justification (done), SARIF and JUnit output (done), grouping of repeated findings (done), YAML via `Microsoft.OpenApi` |
| v0.3 | Roslyn analysis of contract types and endpoints, findings mapped to source locations |
| v0.4 | `Asp.Versioning`-aware rules, baseline files for adopting on legacy APIs |
| v1.0 | Stable rule ids and a compatibility policy for the tool itself, custom rule plugins |

## License

MIT. See `LICENSE`.
