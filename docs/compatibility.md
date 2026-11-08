# Compatibility policy

From 1.0.0, GapWatch follows [semantic versioning](https://semver.org/spec/v2.0.0.html) for the things a build depends on. This page says what those are, which releases may change them, and how.

The short version: **pin the tool version** in your build (see [CI integration](ci-integration.md#1-install-the-tool)). A minor release can find more than the one before it. A major release is the only one that can break the way you call GapWatch or read its output.

## The public contract

| Surface | What is stable |
|---|---|
| Rule ids | `GWnnn` ids and what each one detects. An id is never renumbered, reused or given another meaning, not even in a major release. |
| Rule names and default severities | `GW001 OperationRemoved`, Breaking, and so on, as listed in [rules.md](rules.md). |
| Exit codes | `0` nothing at or above the threshold, `1` at least one finding at or above it, `2` usage error or unreadable input. |
| Commands and options | `compare`, `rules`, `--version`, `--help`, and every option `gapwatch --help` lists, with their values. |
| `gapwatch.json` | Every documented property and value ([configuration.md](configuration.md)). |
| Baseline files | Format `version: 1`, and how entries match findings. |
| Operation and location strings | `GET /pets/{id}`, `GET /pets [2.0]`, `response 200 application/json: $.name` and so on. Suppressions, baselines and SARIF fingerprints are built from them, so they are part of the contract. |
| SARIF | SARIF 2.1.0, the `partialFingerprints` key `gapwatchFinding/v1` and its value, `ruleId`, `level`, and the `properties` keys (`operation`, `location`, `severity`, `component`, `componentPath`, `apiVersion`). |
| JUnit | One test case per finding, grouped by operation; failures at or above the threshold; suppressed findings skipped. |
| Markdown | The first line, `<!-- gapwatch-report -->`, which pipelines use to find and update their comment. |

## Not part of the contract

- **Wording.** Finding messages, rule descriptions, the console layout and the Markdown tables can improve in any release. Baselines use the message only to tell apart findings at the same place, so a reworded message does not invalidate them.
- **Positions and code locations.** Line and column numbers, and the code a finding is mapped to with `--source`, can get more precise in any release.
- **The .NET libraries.** `GapWatch.Core`, `GapWatch.OpenApi`, `GapWatch.Reporting` and `GapWatch.Roslyn` are not published as packages, and their APIs can change in any release.
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

A minor release can therefore turn a green build red: a new rule, or an existing rule that now sees more, can report a change that was always there. That is the reason to pin the version and upgrade on purpose. Release notes list every new rule and every detection change.

`gapwatch.json` rejects unknown properties, so a configuration that uses a property added in a minor release fails on older versions with exit code 2. It never silently means something else.

## How a breaking change is made

1. It waits for the next major release.
2. Where it can, the minor release before it warns: for example, a deprecated option keeps working and prints a warning to stderr.
3. The release notes say what changed and what to do instead.

Rules that should change meaning get a new id instead, and the old one is retired in the next major release.
