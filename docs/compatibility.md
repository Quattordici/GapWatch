# Compatibility policy

From 1.0.0, GapWatch follows [semantic versioning](https://semver.org/spec/v2.0.0.html) for the things a build depends on. This page says what those are, which releases may change them, and how.

The short version: **pin the tool version** in your build (see [CI integration](ci-integration.md#1-install-the-tool)). A minor release can find more than the one before it. A major release is the only one that can break the way you call GapWatch or read its output.

## The public contract

| Surface | What is stable |
|---|---|
| Rule ids | `GWnnn` ids and what each one detects. An id is never renumbered, reused or given another meaning, not even in a major release. The `GW` prefix is reserved: no plugin can use it. |
| Rule names and default severities | `GW001 OperationRemoved`, Breaking, and so on, as listed in [rules.md](rules.md). |
| Exit codes | `0` nothing at or above the threshold, `1` at least one finding at or above it, `2` usage error or unreadable input, which includes a plugin that cannot be loaded or fails. |
| Commands and options | `compare`, `rules`, `--version`, `--help`, and every option `gapwatch --help` lists, with their values. |
| `gapwatch.json` | Every documented property and value ([configuration.md](configuration.md)). |
| Baseline files | Format `version: 1`, and how entries match findings. |
| Operation and location strings | `GET /pets/{id}`, `GET /pets [2.0]`, `response 200 application/json: $.name` and so on. Suppressions, baselines and SARIF fingerprints are built from them, so they are part of the contract. |
| SARIF | SARIF 2.1.0, the `partialFingerprints` key `gapwatchFinding/v1` and its value, `ruleId`, `level`, and the `properties` keys (`operation`, `location`, `severity`, `component`, `componentPath`, `apiVersion`). |
| JUnit | One test case per finding, grouped by operation; failures at or above the threshold; suppressed findings skipped. |
| Markdown | The first line, `<!-- gapwatch-report -->`, which pipelines use to find and update their comment. |
| `GapWatch.Sdk` | The public types plugins compile against, and which plugin versions a release loads. See [below](#gapwatchsdk). |

## Not part of the contract

- **Wording.** Finding messages, rule descriptions, the console layout and the Markdown tables can improve in any release. Baselines use the message only to tell apart findings at the same place, so a reworded message does not invalidate them.
- **Positions and code locations.** Line and column numbers, and the code a finding is mapped to with `--source`, can get more precise in any release.
- **How C# maps to the contract.** With `--input csharp`, what the reader infers from code (binding, required, responses, schemas) can get more precise in any release, like a rule that detects more. Release notes list each change. The operation and location strings it produces follow the same contract as for documents.
- **The .NET libraries other than the SDK.** `GapWatch.Core`, `GapWatch.OpenApi`, `GapWatch.Reporting` and `GapWatch.Roslyn` are not published as packages, and their APIs can change in any release.
- **Plugin rule ids.** A plugin's ids, names and severities are its author's to keep stable. GapWatch only guarantees that they cannot collide with its own.
- **Order of findings** within a severity. It is deterministic for a given version but may change between versions.

## What each kind of release may do

| Change | Patch | Minor | Major |
|---|---|---|---|
| Fix a false positive or a crash | yes | yes | yes |
| Detect a case an existing rule missed | yes | yes | yes |
| Add a rule | no | yes | yes |
| Add an option, a config property or a report field | no | yes | yes |
| Lower a rule's default severity | no | yes | yes |
| Raise a rule's default severity | no | no | yes |
| Remove or rename an option, a config property or a value | no | no | yes |
| Change the format of operation or location strings | no | no | yes |
| Change an exit code's meaning | no | no | yes |
| Retire a rule (it stops firing; its id stays reserved) | no | no | yes |
| Give a rule id another meaning | never | never | never |
| Add a type or member to `GapWatch.Sdk`, or a `FindingLocation` shape | no | yes | yes |
| Add a property to an SDK model record, as an `init` property | no | yes | yes |
| Remove or change an SDK member, add a positional parameter to a record, or add a member to `IRulePlugin` | no | no | yes |
| Stop loading plugins built against an earlier minor version of the same major | no | no | yes |

A minor release can therefore turn a green build red: a new rule, or an existing rule that now sees more, can report a change that was always there. That is the reason to pin the version and upgrade on purpose. Release notes list every new rule and every detection change.

`gapwatch.json` rejects unknown properties, so a configuration that uses a property added in a minor release fails on older versions with exit code 2. It never silently means something else.

## GapWatch.Sdk

`GapWatch.Sdk` is published from 1.2.0 for [custom rule plugins](plugins.md), with the same version as the tool. It holds the contract model (`ApiContract`, `OperationContract`, `SchemaContract` and the rest), `RuleDescriptor`, `Severity`, `Direction`, `FindingLocation`, `IRulePlugin`, `OperationPair`, `PluginFinding`, `RuleContext` and `SchemaWalker`. Every public type and member in it is part of the contract.

- **A release loads a plugin** built against the same major version of the SDK and a minor version no newer than its own. GapWatch 1.4 loads plugins built against 1.2, 1.3 and 1.4.
- **The model records are positional.** Adding a positional parameter changes their constructor and `Deconstruct`, which breaks compiled plugins. New members are therefore added only as `init` properties.
- **`IRulePlugin` does not grow.** Adding a member to it would break every plugin, so new capabilities come as new, optional interfaces.
- **What the reader puts in the model** (which C# it maps, which positions it records) is not part of this guarantee, as for `--input csharp` above. The shape of the model is.

## How a breaking change is made

1. It waits for the next major release.
2. Where it can, the minor release before it warns: for example, a deprecated option keeps working and prints a warning to stderr.
3. The release notes say what changed and what to do instead.

Rules that should change meaning get a new id instead, and the old one is retired in the next major release.
