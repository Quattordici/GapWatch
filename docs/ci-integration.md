# Running GapWatch in CI

This guide explains how to wire `gapwatch compare` into a pull request build on GitHub and Azure DevOps (Services and Server). It describes the steps and the settings that matter; it deliberately contains no ready-made pipeline files, because the build steps around it (how you restore, build and generate your spec) differ per repository.

Each run answers one question: does the contract in this pull request break consumers of the contract on the target branch?

```
gapwatch compare <base> <head> [--config gapwatch.json] [--fail-on breaking|warning|none] [--format ...] [--output <file>]
```

## 1. Install the tool

Install GapWatch as a local tool so the version is pinned in the repository (`.config/dotnet-tools.json`) and every build uses the same rules:

```
dotnet tool install GapWatch  # once, at the repository root; .NET 10 creates the manifest if needed
dotnet tool restore           # in the build
```

(`GapWatch` is a placeholder package id until the NuGet name is confirmed.)

Pinning matters: rule ids are stable, but a newer GapWatch can add rules, so upgrading the tool can surface new findings. Upgrade deliberately.

## 2. Get the base contract

GapWatch compares two files (or two folders of version documents); it never builds or fetches anything itself. Pick one way to obtain the **base** (target branch) document:

| Option | How | Trade-off |
|---|---|---|
| **Committed spec (recommended)** | Commit the generated `openapi.json` (or `.yaml`) and have the build fail when it is stale. Let GapWatch read the base version from git: `gapwatch compare api/openapi.json api/openapi.json --base-ref origin/main` | Simplest and fastest. The spec diff is also visible in the pull request itself. Needs a "spec is up to date" check. |
| Generate from the base commit | Check out the target branch into a separate worktree (`git worktree add ../base origin/main`), build it, and generate the spec there the same way as for the head. | Always accurate, but builds twice and both builds must produce the spec identically. |
| Published artifact | Download the spec your last release published (package feed, build artifact, API gateway). | Compares against what is actually deployed rather than against the target branch. Differences that already exist when you adopt GapWatch can be accepted with a [baseline](configuration.md#baselines). |

Things that break this step in CI:

- **Shallow clones.** `--base-ref origin/main` needs the target branch. Fetch it explicitly (`git fetch origin main`) or disable shallow fetch.
- **Generator drift.** Swashbuckle, NSwag and the built-in `Microsoft.AspNetCore.OpenApi` generator emit different documents for the same code. Generate base and head with the same generator and version, or the diff is noise.
- **Missing base.** When a pull request adds the API for the first time there is no base file. Skip the step in that case rather than comparing against an empty file.

The **head** document is the spec generated (or committed) in the pull request build.

With `Asp.Versioning` there is usually one document per version (`v1.json`, `v2.json`). Put the base documents in one folder and the head documents in another and pass the two folders; GapWatch pairs them by `info.version` and reports a document that disappeared as a removed API version (see [API versions](../README.md#api-versions)). With a committed spec, `--base-ref origin/main` reads the base folder from git as well: `gapwatch compare api/ api/ --base-ref origin/main`.

## 3. Decide what fails the build

Exit codes are a stable contract:

| Code | Meaning |
|---|---|
| 0 | No finding at or above `--fail-on` (default `breaking`). |
| 1 | At least one finding at or above the threshold. |
| 2 | Usage error or unreadable input (also for an invalid `gapwatch.json`). |

Treat 1 and 2 differently if you can: 1 means the API change needs a decision, 2 means the check itself is broken.

Accepting a breaking change is done in `gapwatch.json` (see [configuration](configuration.md)), not by turning the check off: add a suppression with a justification in the same pull request. Reviewers then see the justification in the diff and in every report.

Run the report steps below even when the compare step fails, otherwise the reasons for the failure are not published.

## 4. GitHub

### Pull request comment

1. Run `gapwatch compare base.json head.json --format markdown --output gapwatch.md`.
2. Post `gapwatch.md` as a pull request comment. The report starts with `<!-- gapwatch-report -->`: look for an existing comment containing that marker and update it instead of adding a new comment on every push.
3. The job needs permission to write pull request comments (`pull-requests: write` for the workflow token).

### Code scanning (SARIF)

1. Run `gapwatch compare base.json head.json --format sarif --output gapwatch.sarif` **from the repository root**, passing the head document as a path relative to the root (`api/openapi.json`). GitHub resolves relative result paths against the repository root, so this makes alerts point at the right file.
2. Upload it with GitHub's `github/codeql-action/upload-sarif` action (`sarif_file: gapwatch.sarif`). Set `category` (for example `gapwatch`) if you upload more than one SARIF file per commit, or when one repository has several APIs: uploads with the same tool and category replace each other.
3. The job needs `security-events: write`. For private and internal repositories, code scanning requires GitHub Code Security to be enabled.

What to expect:

- Alerts are anchored at the line of the change in the head document (the member's key, or the closest enclosing element that still exists). Pass `--source <dir>` (relative to the repository root, like the head path) to anchor them at the C# code instead: the DTO property, action parameter or endpoint behind the change, with the OpenAPI document as a related location. GitHub then annotates the C# change in the pull request. The alert title carries the operation, and the result's logical location carries the exact place (`GET /pets > response 200 application/json: $[].id`).
- Severity maps to SARIF levels: Breaking `error`, Warning `warning`, Info `note`.
- Alerts keep their identity across runs through `partialFingerprints` (rule, operation, location), so rewording a message or moving the change to another line does not reopen alerts.
- GitHub limits a run to 25,000 results and shows the top 5,000; GapWatch stays far below that in practice.
- Removed operations have no place in the head document; they are anchored at its first line.

> Not yet verified against a live repository: how code scanning displays results that carry SARIF `suppressions` (GapWatch emits suppressed findings with `kind: external` and the justification). Confirm on a first upload, and report what you see.

## 5. Azure DevOps

### Tests tab (JUnit)

1. Run `gapwatch compare base.json head.json --format junit --output gapwatch-junit.xml --fail-on breaking`.
2. Publish the file with the **Publish Test Results** task (format `JUnit`), and run that task even when the compare step failed (condition "even if a previous task has failed").
3. Do not also set "fail if there are test failures" unless you want two failing steps for one problem: the compare step's exit code already fails the build, and the XML agrees with it.

How the results appear:

| GapWatch | Azure DevOps Tests tab |
|---|---|
| Finding at or above `--fail-on` | Failed test; the error message is the finding's sentence |
| Finding below the threshold | Passed test; the details are in the console log |
| Suppressed finding | Not executed; the message is `Suppressed: <justification>` |
| No findings | One passed test, so the run is never empty |

Each test is named `GW025 RequiredPropertyRemoved: <location>` and its test file is the operation (`GET /pets`), so you can filter by rule or by endpoint. This works on Azure DevOps Server as well.

### Pull request comment

There is no built-in task that posts a file as a pull request comment. Post `gapwatch.md` (from `--format markdown`) with the Azure DevOps REST API (*Pull Request Threads - Create*), authenticating with the pipeline's `System.AccessToken`:

- The project's build service identity needs **Contribute to pull requests** on the repository.
- To avoid a new thread per push, list the existing threads first and update the one whose first comment contains `<!-- gapwatch-report -->`.
- Pull request comments support Markdown tables and block quotes. Microsoft documents the collapsible `<details>` sections for wikis, not explicitly for pull requests, so check the first comment; if they do not collapse, the content is still all there. The `> [!NOTE]` block that lists unused suppressions may render as a plain quote.

### SARIF

- **Azure DevOps Services with GitHub Advanced Security for Azure DevOps:** publish third-party SARIF with the `AdvancedSecurity-Publish@1` task (`SarifsInputDirectory` pointing at the folder with `gapwatch.sarif`, and a `Category` such as `gapwatch`). Results then appear in the Advanced Security code scanning tab. The file path in each result must be accessible to the task on the agent, so generate the SARIF from the repository root with a relative head path, as for GitHub.
- **Without Advanced Security (including most Azure DevOps Server installations):** the Tests tab and the pull request comment above give the same information. A SARIF viewer extension from the Marketplace can show the file as a build tab; check that the extension is available for your server version.

## 6. A checklist for the first run

- [ ] Base and head are produced by the same generator and version.
- [ ] The compare step runs from the repository root with relative paths.
- [ ] The report steps run even when the compare step fails.
- [ ] Exit code 2 is visible as "the check is broken", not as "the API is broken".
- [ ] Accepted breaking changes are suppressions with justifications, reviewed in the pull request.
- [ ] One SARIF upload and one pull request comment were checked by hand.
