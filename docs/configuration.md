# Configuring GapWatch

Pass a configuration file with `--config`:

```
gapwatch compare base.json head.json --config gapwatch.json
```

The file is optional; without it every rule reports with its default severity. GapWatch does not look for a file on its own, so a build never changes behaviour because a file happened to be present.

## Format

```jsonc
{
  // Severity per rule id: "breaking", "warning", "info" or "off".
  "rules": {
    "GW004": "breaking", // we publish generated clients, so an operationId change is breaking for us
    "GW003": "off"       // new operations are not interesting in this repository
  },

  // Justified exceptions for individual findings.
  "suppressions": [
    {
      "rule": "GW025",
      "operation": "GET /pets",
      "location": "response 200 application/json: $[].nickname",
      "justification": "Never populated in production; removal agreed with all consumers (ADR-12).",
      "expires": "2027-01-01" // optional: reported again from this date
    }
  ],

  // Lowest severity that fails the build: "breaking" (default), "warning" or "none". --fail-on wins over it.
  "failOn": "breaking",

  // How API versions are recognized (the defaults shown match Asp.Versioning). Or "versioning": false.
  "versioning": {
    "pathSegment": "^v\\d+(\\.\\d+)?$",
    "parameters": ["api-version"],
    "mediaTypeParameter": "v"
  }
}
```

Comments and trailing commas are allowed. Unknown properties, unknown rule ids and unknown severities are errors (exit code 2), so a typo never silently switches a check off.

## Severity overrides

| Value | Effect |
|---|---|
| `breaking`, `warning`, `info` | Findings of the rule report with this severity. `--fail-on` and the report summary use the overridden severity. |
| `off` | Findings of the rule are dropped. They do not appear anywhere in the report. |

Use an override when your organisation judges a whole rule differently from the default, for example treating `GW004 OperationIdChanged` as breaking because you ship generated clients. Use a suppression for a single, deliberate change.

## Suppressions

| Property | Required | Meaning |
|---|---|---|
| `rule` | yes | Rule id, e.g. `GW025`. |
| `justification` | yes | Why the change is acceptable. Must not be blank; it is printed next to the suppressed finding in every report. |
| `operation` | no | Operation as shown in the report, e.g. `GET /pets/{id}`. Omit to match every operation. |
| `location` | no | Location as shown in the report, e.g. `parameter 'q' (query)`. Omit to match every location. |
| `expires` | no | A date (`yyyy-MM-dd`, UTC). From that day the suppression no longer applies: its findings count again, and it is listed with the unused suppressions so it can be removed or renewed. |

Use `expires` for changes that are accepted for a while, such as "clients have until the end of the quarter to move". The date is checked against today's date in UTC, so the same run on a later day can fail. That is the point of an expiry, but it means a build can turn red without a code change.

`operation` and `location` match literally and case-sensitively, except that `*` matches any run of characters (`GET /pets*`, `*.nickname`). Copy the values from a report to get them right.

A suppressed finding:

- does not count towards the summary or the exit code;
- still appears in a separate **Suppressed** section, with its justification, so reviewers can see what was accepted.

A suppression that matches no finding is listed under **Unused suppressions**. That usually means the change was reverted or has shipped and the base contract caught up; remove the entry.

Severity overrides are applied before suppressions, and the first matching suppression wins.

## Baselines

A baseline accepts a whole set of existing findings at once. It is meant for adopting GapWatch when the base contract is already far behind the head: typically when you compare against the spec of your last release (see [CI integration](ci-integration.md)) and dozens of changes have accumulated since. A pull request compared against its target branch rarely needs one, because it only shows that pull request's own changes.

```
gapwatch compare release-2.3.json api/openapi.json --write-baseline gapwatch-baseline.json   # once, then commit the file
gapwatch compare release-2.3.json api/openapi.json --baseline gapwatch-baseline.json         # in the build
```

`--write-baseline` writes every finding of the run (after `gapwatch.json` is applied) to the file and then reports with it, so that run passes. The file is plain, sorted JSON, so it diffs cleanly in review:

```json
{
  "version": 1,
  "justification": "Existing differences accepted when the baseline was written.",
  "findings": [
    {
      "rule": "GW026",
      "operation": "GET /pets",
      "location": "response 200 application/json: $[].tag",
      "message": "Optional response property 'tag' was removed."
    }
  ]
}
```

Edit `justification` to say why these findings are accepted. One justification covers the whole file, and it is printed next to every baselined finding. When you rewrite the baseline, the justification is kept: it comes from the `--baseline` file in use, or else from the file being replaced.

How a baseline differs from suppressions:

| | Suppression in `gapwatch.json` | Baseline |
|---|---|---|
| Written by | A person, for one deliberate change | GapWatch, for everything that exists now |
| Matching | Rule, plus optional `operation` and `location` with `*` wildcards | Rule, operation and location, exactly; `message` only tells apart findings at the same place |
| Covers | Every finding it matches | One finding per entry, so a second change at the same place still reports |
| Justification | One per suppression | One per file |

Baselined findings are reported like suppressed ones: they do not count towards the summary or the exit code, and they are listed with the justification. An entry that matches nothing is listed as an unused `baseline:` entry, because the change was fixed, reverted or released. Rewrite the baseline to drop it. A baseline is applied after `gapwatch.json`: overrides and disabled rules come first, and a finding suppressed in `gapwatch.json` keeps its own justification.

## API versions

The `versioning` section controls how GW060 to GW063 find the API version of an operation. Every key is optional, and a missing key keeps its default.

| Key | Default | Meaning |
|---|---|---|
| `pathSegment` | `^v\d+(\.\d+)?$` | Regular expression (case-insensitive) for a path segment that is the version, e.g. `/v1/pets`. This is the default `VVV` substitution format with a `v` prefix. `null` stops looking at paths. |
| `parameters` | `["api-version"]` | Query or header parameters (case-insensitive) whose enum lists the versions. Add your header name if you use header versioning, e.g. `"x-api-version"`. `null` or `[]` stops looking at parameters. |
| `mediaTypeParameter` | `"v"` | Media type parameter that carries the version, e.g. `application/json;v=2.0`. `null` stops looking at media types. |

`"versioning": false` turns detection off, and every change is reported with the version-unaware rules (GW001, GW002, GW003, GW023, GW043). Folder mode still labels each operation with its document's version, because that is how the documents are told apart.

Severity overrides, suppressions and baselines apply to the version findings like to any other: to treat retiring a deprecated version as expected, set `"GW061": "info"` and keep GW060 Breaking.

## Failing the build

`failOn` sets the lowest severity that makes the exit code 1: `breaking` (the default), `warning` or `none`. The `--fail-on` option wins over it, so a pipeline can still tighten or relax one run.

## Not supported yet

- Discovering `gapwatch.json` automatically. A build should not change behaviour because a file happened to be present, so the file is always passed with `--config`.
- Setting the report format from the file.
