# Custom rule plugins

A plugin adds rules of your own, such as "every new endpoint requires authentication" or "every string a client sends has a `maxLength`". Plugin rules run in the same check as the built-in rules. Their findings go through severity overrides, suppressions, baselines, `--fail-on`, every report format and the exit code exactly like `GWnnn` findings.

```
gapwatch compare base.json head.json --plugin tools/Acme.GapWatch.Rules.dll
```

A plugin is a .NET assembly with a class that implements `IRulePlugin` from the `GapWatch.Sdk` package. It sees the same normalized contract as the built-in rules, so it works for OpenAPI documents and for `--input csharp` alike.

## Trust

**Plugins run with the full trust of the GapWatch process**, like Roslyn analyzers or MSBuild tasks. A plugin can read files, use the network and run programs. Load only assemblies you would also let run in your build. GapWatch never loads an assembly nobody named: there is no folder scan and no package download.

## Writing a plugin

Create a class library for .NET 10 and reference the SDK. `ExcludeAssets="runtime"` leaves `GapWatch.Sdk.dll` out of your output, because GapWatch always uses its own copy:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="GapWatch.Sdk" Version="1.2.0" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

Then implement `IRulePlugin`:

```csharp
using GapWatch.Core.Findings;
using GapWatch.Sdk;

public sealed class AcmeRules : IRulePlugin
{
    public static readonly RuleDescriptor NewOperationWithoutSecurity = new(
        "ACME001", nameof(NewOperationWithoutSecurity), Severity.Breaking,
        "A new operation allows anonymous access.")
    {
        HelpUri = new Uri("https://wiki.acme.example/api-rules#ACME001"), // optional; SARIF links to it
    };

    public string Prefix => "ACME";

    public IReadOnlyList<RuleDescriptor> Rules { get; } = [NewOperationWithoutSecurity];

    public IEnumerable<PluginFinding> Check(OperationPair pair, RuleContext context)
    {
        if (pair is { Baseline: null, Head: { } head } && (head.Security.Count == 0 || head.Security.Contains("none")))
        {
            yield return new PluginFinding("ACME001", FindingLocation.Operation.Instance, $"New operation '{pair.Operation}' allows anonymous access.");
        }
    }
}
```

The class must be public, not abstract, and have a public parameterless constructor. An assembly can hold several plugin classes, each with its own prefix.

### What a rule sees

`Check` is called once for each operation pair, in a fixed order, on one thread:

| Pair | `Baseline` | `Head` |
|---|---|---|
| The operation exists in both versions | set | set |
| It was added | null | set |
| It was removed | set | null |

Operations pair the way the built-in rules pair them: by method, by path with parameter names erased (so renaming `{id}` to `{petId}` keeps the pair), and by API version. `pair.Operation` is the operation string the finding is reported under. `context.Baseline` and `context.Head` are the whole contracts, for a rule that looks across operations.

### Direction

Whether a change breaks a consumer depends on whether consumers send the value or read it (see [How direction changes the verdict](../README.md#how-direction-changes-the-verdict)). `SchemaWalker.Walk(pair)` visits every schema node of an operation in both versions and gives its direction:

```csharp
foreach (var node in SchemaWalker.Walk(pair))
{
    // node.At: the location to report, e.g. request body (application/json): $.note
    // node.Before / node.After: the schema in each version, null where the node does not exist
    // node.Direction: Request for parameters and request bodies, Response for responses
}
```

The walker visits parameters, request body media types and response media types, then the properties (`$.name`), array items (`$[]`) and `oneOf`/`anyOf` alternatives (`$<Cat>`) of each. A read-only property is not part of a request and a write-only one is not part of a response, so neither is visited there. It pairs parameters, responses, properties and alternatives exactly as the built-in rules do. Every rule should state which direction it applies to and why, as the built-in rules do.

### Findings

A `PluginFinding` has a rule id, a location and a message:

- **The rule id** must be one of the plugin's `Rules`.
- **The location** is one of the fixed `FindingLocation` shapes: `Operation`, `Parameter`, `RequestBody`, `RequestContent`, `Response`, `ResponseMediaType`, `ResponseContent` and `Security`. Suppressions, baselines and SARIF fingerprints match on the location string, so a plugin cannot invent a new one. Take it from `node.At` when you report a schema node.
- **The message** is one complete sentence.

GapWatch fills in the rest. The position and document come from the head operation, never from the plugin. A plugin does not see the built-in findings and cannot change or remove them: changing what a built-in rule reports is what [configuration](configuration.md) is for.

A rule must not read configuration. Its severity, `off` and its suppressions are set in `gapwatch.json`, like any other rule's. It must return the same findings for the same input.

## Rule ids

A plugin id is its prefix followed by 1 to 4 digits: `ACME001`, `PAY42`.

| Check | Rule |
|---|---|
| Prefix | 2 to 8 uppercase letters, A to Z |
| Reserved | A prefix must not start with `GW` (so `GWX` is rejected too). `GW` ids are GapWatch's own, so a plugin id can never collide with one, now or later. |
| Id | Exactly the prefix and 1 to 4 digits |
| Unique | Each id once per plugin, and each prefix once among the loaded plugins |

Suppressions and baselines in your repositories depend on your ids, so treat them as GapWatch treats its own: never renumber an id, reuse one or give it another meaning.

## Loading plugins

Name each plugin assembly in `gapwatch.json`, relative to the file, or with `--plugin`, relative to the working directory. `--plugin` can be repeated, and it adds to the plugins in the file:

```jsonc
{
  "plugins": [ { "path": "tools/gapwatch/Acme.GapWatch.Rules.dll" } ],
  "rules": { "ACME002": "info" }
}
```

```
gapwatch rules --config gapwatch.json      # lists the built-in rules, then ACME001 (plugin Acme.GapWatch.Rules) ...
```

Build the plugin with `dotnet publish` and point at the published assembly. Each assembly gets its own load context: its dependencies come from its `.deps.json`, or from its folder when there is none, so they cannot clash with GapWatch's or another plugin's. `GapWatch.Sdk` always comes from GapWatch. How the assembly reaches your build agent is up to you: commit it, restore it from your own feed, or build it in an earlier step.

`gapwatch.json` is checked once the plugins are loaded, so an override or a suppression for `ACME001` is an error when the plugin providing it is not loaded. A suppression therefore never stays quietly unused after a plugin is removed.

## When a plugin fails

A plugin never fails quietly. Each of these stops the run with exit code 2 and a message that names the plugin:

- the file is missing or is not a .NET assembly;
- it has no public class that implements `IRulePlugin`, or a plugin's constructor throws;
- it was built against an unsupported SDK version (see below);
- a prefix or rule id breaks the rules above;
- `Check` throws, or returns a finding for a rule the plugin does not declare. The message also names the operation.

A check that silently ran fewer rules than configured would pass the changes those rules exist to catch.

## Versions

`GapWatch.Sdk` has the same version as the tool. GapWatch runs a plugin built against the same major version of the SDK and a minor version no newer than its own. GapWatch 1.4 runs plugins built against SDK 1.2 to 1.4. GapWatch 1.2 refuses a plugin built against 1.4, which may use members 1.2 does not have. The SDK follows the [compatibility policy](compatibility.md#gapwatchsdk): a minor release only adds to it.

## The sample plugin

[`samples/Acme.GapWatch.Rules`](../samples/Acme.GapWatch.Rules) has two rules to copy from. Its tests are in `tests/GapWatch.Tests/Plugins/SamplePluginTests.cs`.

| Id | Name | Severity | Direction | Before | After |
|---|---|---|---|---|---|
| ACME001 | NewOperationWithoutSecurity | Breaking | None: it looks at added operations only | (no `POST /invoices`) | `POST /invoices` without a security requirement |
| ACME002 | UnboundedRequestString | Warning | Request only | `{ "id": integer }` in a request body | `{ "id": integer, "note": string }`, without `maxLength` |

- **ACME001** reports a new operation that allows anonymous access. Existing open operations are not reported, so adopting the rule needs no baseline.
- **ACME002** reports a new string that clients send and that has no `maxLength`, `format` or `enum`. In a request, it is an input validation gap. The same string in a response limits nobody, so it is not reported there.

## Not supported

- **Isolation.** Plugins run in the GapWatch process; see [Trust](#trust).
- **Package discovery.** GapWatch does not download plugins from NuGet. Name the assembly.
- **New location shapes, input formats or report formats.** Plugins add rules only.
