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
      "justification": "Never populated in production; removal agreed with all consumers (ADR-12)."
    }
  ]
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

`operation` and `location` match literally and case-sensitively, except that `*` matches any run of characters (`GET /pets*`, `*.nickname`). Copy the values from a report to get them right.

A suppressed finding:

- does not count towards the summary or the exit code;
- still appears in a separate **Suppressed** section, with its justification, so reviewers can see what was accepted.

A suppression that matches no finding is listed under **Unused suppressions**. That usually means the change was reverted or has shipped and the base contract caught up; remove the entry.

Severity overrides are applied before suppressions, and the first matching suppression wins.

## Not supported yet

- Discovering `gapwatch.json` automatically, and setting `--fail-on` or the format from the file.
- Expiry dates on suppressions.
- Baseline files that suppress every existing finding at once (planned for v0.4).
