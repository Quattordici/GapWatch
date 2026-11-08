# GapWatch

GapWatch finds **breaking changes between two versions of an HTTP API contract** before they reach your consumers. It compares a baseline OpenAPI document with a proposed one, or two versions of ASP.NET Core code, and explains every consumer-visible difference, with a rule id, a precise location and a severity.

> **Status: v1.2.** Rule ids, exit codes, options, configuration and report formats are stable under semantic versioning; see the [compatibility policy](docs/compatibility.md). See also [Limitations](#limitations).

## Why another API diff tool

- **Direction-aware.** The same change is breaking or safe depending on who produces the value. A new enum value in a *response* can break a client's `switch`; in a *request* it is harmless. Removing an enum value is the opposite. GapWatch models this explicitly instead of reporting "something changed".
- **Explainable.** Every finding carries a stable rule id (`GW025`), the operation, the location inside it (`response 200 application/json: $[].name`) and a complete sentence.
- **Built for .NET teams.** Understands `readOnly`/`writeOnly`, nullable handling, `int32`→`int64` widening, `operationId` changes that break generated clients, path-parameter renames, and security changes.
- **Dependency-free core.** The engine, the JSON reader and the reports use only the .NET base class library. The only package dependency is YamlDotNet, for YAML input.

## Quick start

```
dotnet build
dotnet run --project src/GapWatch.Cli -- compare before.json after.json   # .yaml / .yml work too
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
gapwatch compare <base> <head> [--format console|markdown|sarif|junit] [--output <file>] [--fail-on breaking|warning|none] [--config <file>] [--baseline <file>] [--write-baseline <file>] [--source <dir>] [--base-ref <rev>] [--input openapi|csharp] [--plugin <file>]...
gapwatch rules [--format console|markdown] [--config <file>] [--plugin <file>]...
gapwatch --version | --help
```

- `<base>` is the contract consumers rely on today; `<head>` is the proposed change. With one OpenAPI document per API version (the usual `Asp.Versioning` setup), pass two folders instead of two files; see [API versions](#api-versions).
- `--format markdown` produces a report for pull request comments. It starts with a hidden marker (`<!-- gapwatch-report -->`) so a pipeline can find and update one comment instead of posting a new one each run.
- `--format sarif` writes a SARIF 2.1.0 log for code-scanning views (GitHub code scanning, the Azure DevOps SARIF viewer, IDE SARIF viewers). See [Output formats](#output-formats).
- `--format junit` writes JUnit XML for CI test tabs (Azure DevOps, Jenkins, GitLab).
- `--output` writes the report to a file and prints a one-line summary.
- `--config` reads a `gapwatch.json` with severity overrides and suppressions (see [Configuration](#configuration)).
- `--baseline` accepts the findings listed in a baseline file, and `--write-baseline` writes one from the current run. Baselines are for adopting GapWatch against a base that is far behind, such as the last release (see [Configuration](docs/configuration.md#baselines)).
- `--base-ref` reads `<base>` from a git revision instead of the working tree, e.g. `gapwatch compare api/openapi.json api/openapi.json --base-ref origin/main`. It works for files and folders, and only reads from git.
- `--source` points at the C# source of the head API and maps each finding to the code behind it (see [Mapping findings to C# code](#mapping-findings-to-c-code)).
- `--plugin` loads [custom rules](docs/plugins.md) from an assembly, in addition to the `plugins` in `gapwatch.json`. It can be repeated. Plugins run with full trust, so load only assemblies you would also run in your build.
- `--input csharp` reads the contract from ASP.NET Core source instead of OpenAPI documents: `<base>` and `<head>` are source folders, e.g. `gapwatch compare src/Api src/Api --input csharp --base-ref origin/main`. See [Reading the contract from C#](docs/csharp-input.md).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | No finding at or above the `--fail-on` threshold (default threshold: `breaking`, or `failOn` in `gapwatch.json`). |
| 1 | At least one finding at or above the threshold. |
| 2 | Usage error or unreadable input, including a plugin that cannot be loaded or fails. |

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

Every finding carries the line and column of the change in the head document (JSON or YAML): the changed element, or the closest enclosing element that still exists, such as the schema a property was removed from. A change inside a shared component points at the component's definition. The console report prints it as `at api/openapi.json:42:9`, the Markdown report as the line number under the location, and SARIF results use it as their region, next to a logical location (`GET /pets > response 200 application/json: $[].id`). A removed operation has no place in the head document and is anchored at line 1.

### Mapping findings to C# code

With `--source src/Petstore.Api`, GapWatch reads the C# files of the head API with Roslyn and adds a code location to each finding it can place:

| Finding | Code location |
|---|---|
| Change inside a component schema (`Pet $.owner.name`) | The DTO property (`Owner.Name`), following `[JsonPropertyName]`, base classes, positional records, nullable and collection types. If the property is gone from the code, the type. |
| Parameter change | The action or handler parameter, or the property of an `[AsParameters]` type, honouring `[FromQuery(Name = "...")]` and the other binding attributes. |
| Anything else on an operation | The controller action, or the `MapGet`/`MapPost`/... call. |
| Removed operation | None: it no longer exists in the head code. |

Operations are matched by method and route, using the same reader as `--input csharp` ([C# input](docs/csharp-input.md)), so binding and routing follow the same rules in both. An endpoint mapped to several API versions with a `{version:apiVersion}` route segment matches each version's path. Attribute routing is resolved the way ASP.NET Core does it: class and action `[Route]`, `[HttpGet("...")]` templates, `[controller]`, `[action]` and `[area]` tokens, `~/` and `/` overrides, `Async` suffixes, and constraints, defaults and optional or catch-all markers removed. Minimal APIs are matched through their `MapGroup` prefixes, chained or held in a variable or field, and route constants are evaluated. The console prints `code at src/Models/Pet.cs:12:5`, Markdown adds the file and line, JUnit adds it to the failure text, and SARIF makes the code the primary location with the OpenAPI document as a related location, so code scanning annotates the C# change in a pull request.

How it reads code:

- **Source files only.** Every `.cs` file under the directory is parsed and compiled together against the running .NET runtime. Nothing is restored or built, and no MSBuild is involved, so it runs on any checkout without a build (about a second for a small API). `bin`, `obj`, `.git` and `node_modules` are skipped.
- **ASP.NET Core attributes and `Map*` calls are recognized by name**, because ASP.NET Core is not referenced. A type of your own called `HttpGetAttribute` would confuse it.
- **Best effort.** Anything that cannot be matched unambiguously gets no code location instead of a wrong one: a schema name declared by two types, generic DTOs (Swashbuckle names them `PetPagedResult`, `Microsoft.AspNetCore.OpenApi` `PagedResultOfPet`), custom schema ids, routes built at runtime, and endpoints mapped in extension methods whose group prefix comes from the caller.

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

## Running in CI

[docs/ci-integration.md](docs/ci-integration.md) covers getting the base contract, choosing what fails the build, and publishing the results on GitHub (pull request comment, code scanning) and Azure DevOps (Tests tab, pull request comment, Advanced Security).

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

38 rules across operations, parameters, schemas (including value constraints), bodies, responses, security and API versions. See [docs/rules.md](docs/rules.md), or run `gapwatch rules`. Rule ids are stable: they never change meaning and are never reused.

Teams can add rules of their own as [plugins](docs/plugins.md): a .NET assembly that implements `IRulePlugin` from `GapWatch.Sdk`. Plugin ids have their own prefix, such as `ACME001` (`GW` is reserved), and their findings go through configuration, baselines and every report format like built-in ones.

### API versions

GapWatch recognizes `Asp.Versioning` setups and reports a version as one decision instead of one finding per endpoint. A version is read from the document it is in (folder mode), a path segment such as `/v1/pets`, the enum of an `api-version` query or header parameter, or the `v` media type parameter (`application/json;v=2.0`). `v1`, `1` and `1.0` are the same version.

| Rule | Severity | Replaces, for those operations |
|---|---|---|
| GW060 ApiVersionRemoved | Breaking | GW001, GW023, GW043 |
| GW061 DeprecatedApiVersionRemoved | Warning | GW001, GW023, GW043 |
| GW062 ApiVersionAdded | Info | GW003 |
| GW063 ApiVersionDeprecated | Info | GW002 |

A version is removed only when no operation in the head has it any more; removing one endpoint of a version that stays is still GW001. An `api-version` parameter that gains an enum of only the versions its operation already serves (Asp.Versioning 10 started emitting one) declares what was already true, so it is neither GW023 nor GW062. A version counts as deprecated when every operation of the version is marked `deprecated`, so GW061 means it was retired on the planned path. One finding is still reported per operation, so counts and exit codes stay per operation, and reports group them under the version.

**Folder mode.** `gapwatch compare base/ head/` reads every `.json`, `.yaml` and `.yml` file in each folder (not recursive) and pairs the documents by `info.version`, or by file name when an `info.version` is missing or repeated. Operations are shown with their version, for example `GET /pets [2.0]`, and a document on one side only is a removed or added version. The detection patterns can be changed or turned off in `gapwatch.json` (see [Configuration](docs/configuration.md#api-versions)).

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
| `maxLength`, `maximum` or `maxItems` lowered (or `min*` raised) | Breaking | Safe |
| Such a bound raised or removed | Safe | Warning |
| `pattern` added | Breaking | Safe |
| `additionalProperties: false` added | Warning | Safe |

## Architecture

```
src/
  GapWatch.Sdk         What plugins compile against: the contract model, rule descriptors, finding locations, IRulePlugin and SchemaWalker. Published; no I/O, no dependencies.
  GapWatch.Core        Rule catalog and rule set, comparison engine, plugin runner, policy (overrides, suppressions). No I/O, no dependencies but the SDK.
  GapWatch.OpenApi     OpenAPI 3.x readers behind IContractReader: JSON (System.Text.Json) and YAML (YamlDotNet, converted to JSON first).
  GapWatch.Reporting   Console, Markdown, SARIF and JUnit renderers, rule list. BCL only.
  GapWatch.Roslyn      Reads the contract from C# (--input csharp) and maps findings to C# code (controllers, minimal APIs, DTOs) with Microsoft.CodeAnalysis.CSharp; source files only.
  GapWatch.Cli         The `gapwatch` dotnet tool, the gapwatch.json reader, the plugin loader (one AssemblyLoadContext per plugin), and ContractInputs, which reads both sides from the working tree or a git revision. GapWatchCli.Run is console-free and unit tested.
samples/
  Acme.GapWatch.Rules  A sample plugin with two rules (docs/plugins.md).
tests/
  GapWatch.Tests       TUnit + TUnit.Mocks + Shouldly. One test per rule and direction, reader tests, golden reports, CLI tests.
```

Every source format compiles into one normalized `ApiContract`; rules only ever see that model. That includes C# source: `CSharpContractReader` builds the same model from controllers, minimal APIs and DTOs, so it added no rules. `CompatibilityCheck.Run` in Core runs the check in its one correct order: compare (built-in and plugin rules), turn per-operation findings about API versions into version findings (GW060 to GW063), apply the configured policy, then the baseline. Code mapping with `--source` happens after that, on the findings, so it never influences a verdict.

## Running the tests

`dotnet test` runs on the Microsoft Testing Platform (configured in `global.json`, requires the .NET 10 SDK). The tests use TUnit (`TUnit.Core` + `TUnit.Engine`, without `TUnit.Assertions`), `TUnit.Mocks` for test doubles and Shouldly for assertions. Package versions are pinned in `Directory.Packages.props`.

To regenerate the golden reports after an intentional output change, run the tests with `GAPWATCH_UPDATE_GOLDEN=1` and review the diff.

## Limitations

- **YAML 1.2 only.** YAML documents are read with YAML 1.2 core-schema typing, as OpenAPI requires. Unquoted `yes`/`no`/`on`/`off` are strings, not booleans (YAML 1.1 tools disagree here). Merge keys (`<<`) and multi-document files are rejected. Write `openapi: 3.1.0`, not `openapi: 3.1`, because an unquoted `3.1` is a number.
- **Local references only.** External `$ref`s are rejected with a clear message; bundle the document first. Swagger 2.0 is not supported.
- **Common value constraints only.** Length, item-count and numeric bounds, `pattern`, `additionalProperties: false` and `discriminator` are compared (GW032 to GW037). `multipleOf`, `uniqueItems`, `minProperties`, `maxProperties` and `const` are not. Whether one `pattern` accepts a subset of another is not decided, so a changed pattern is a Warning to review.
- **API versions from the spec, or from code with `--input csharp`.** `[ApiVersion]` and `[MapToApiVersion]` are read when the contract comes from C#; `--source` uses them only to match versioned routes, not to detect version changes. A removed version exists in no head document, so its SARIF result points at the first head document, line 1, as a removed operation does.
- **Plugins run in process, with full trust.** There is no sandbox, and plugins are loaded only from paths you name, never from NuGet. See [plugins](docs/plugins.md#trust).
- **Explicit configuration only.** `gapwatch.json` must be passed with `--config`; it is not discovered automatically.
- **Code mapping is best effort.** See [Mapping findings to C# code](#mapping-findings-to-c-code) for what is not matched.
- **C# input sees what the code says statically.** Runtime-built routes, filters, source generators and validation libraries are not seen; see the [limits](docs/csharp-input.md#limits).

## Roadmap

| Version | Scope |
|---|---|
| v0.1 | OpenAPI JSON diff, 28 rules, console and Markdown reports (done) |
| v0.2 | `gapwatch.json` severity overrides and suppressions that require a justification (done), SARIF and JUnit output (done), grouping of repeated findings (done), YAML input (done), Azure DevOps and GitHub integration docs (done) |
| v0.3 | Findings mapped to source locations: line and column in the OpenAPI document (done), Roslyn mapping of operations, parameters and schemas to controllers, minimal API endpoints and DTOs (done) |
| v0.4 | Baseline files for adopting on legacy APIs (done), `Asp.Versioning`-aware rules and folder mode (done) |
| v0.5 | Value-constraint rules GW032 to GW037 (done), `failOn` and suppression expiry in `gapwatch.json` (done), `--base-ref` (done) |
| v1.0 | Stable rule ids and a [compatibility policy](docs/compatibility.md) for the tool itself (done) |
| v1.1 | [Contract read from C# source](docs/csharp-input.md) with `--input csharp`, no new rule ids (done) |
| v1.2 | [Custom rule plugins](docs/plugins.md) and the `GapWatch.Sdk` package (this release) |

## License

MIT. See `LICENSE`.
