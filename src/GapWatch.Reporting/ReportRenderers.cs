using System.Globalization;
using System.Text;
using GapWatch.Core.Findings;

namespace GapWatch.Reporting;

/// <summary>Labels shown in a report header, plus settings that formats need to describe the run.</summary>
/// <param name="BaseLabel">Where the baseline came from (usually a file name).</param>
/// <param name="HeadLabel">Where the proposed contract came from.</param>
public sealed record ReportContext(string BaseLabel, string HeadLabel)
{
    /// <summary>GapWatch version for formats that record the producing tool (SARIF). <c>null</c> omits it.</summary>
    public string? ToolVersion { get; init; }

    /// <summary>
    /// The <c>--fail-on</c> threshold, for formats that mark findings as failed (JUnit). <c>null</c> means nothing fails.
    /// </summary>
    public Severity? FailOn { get; init; } = Severity.Breaking;

    /// <summary>
    /// In multi-document mode, the head document a location falls back to when a finding has no
    /// <see cref="Finding.Document"/> (a removed API version exists in no head document). Null for a single head
    /// document, where <see cref="HeadLabel"/> is that document.
    /// </summary>
    public string? FallbackHeadDocument { get; init; }

    /// <summary>The rules of the run, for formats that list them (SARIF): the built-in rules unless plugins are loaded.</summary>
    public RuleSet Rules { get; init; } = RuleSet.BuiltIn;
}

/// <summary>Turns a <see cref="ComparisonResult"/> into text.</summary>
public interface IReportRenderer
{
    /// <summary>Renders the result. Output is deterministic for identical input.</summary>
    string Render(ComparisonResult result, ReportContext context);
}

/// <summary>Plain-text report for terminals and build logs.</summary>
public sealed class ConsoleReportRenderer : IReportRenderer
{
    public string Render(ComparisonResult result, ReportContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var text = new StringBuilder();
        text.Append("GapWatch: ").Append(context.BaseLabel).Append(" -> ").Append(context.HeadLabel).Append('\n');
        text.Append(Summary.Describe(result)).Append('\n');

        if (result.Findings.Count == 0)
        {
            text.Append(result.Suppressed.Count == 0
                ? "\nNo differences that affect consumers were found.\n"
                : "\nNo unsuppressed differences that affect consumers were found.\n");
        }

        foreach (var severity in new[] { Severity.Breaking, Severity.Warning, Severity.Info })
        {
            var group = result.Findings.Where(f => f.Severity == severity).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            text.Append('\n').Append(Summary.Heading(severity).ToUpperInvariant()).Append(" (").Append(group.Count).Append(")\n");
            foreach (var change in FindingGroup.Group(group))
            {
                var finding = change.First;
                if (change.IsShared)
                {
                    // One change in a shared component schema or API version, seen through every operation it affects.
                    text.Append("  [").Append(finding.Rule.Id).Append("] ").Append(finding.GroupLabel)
                        .Append(", affects ").Append(change.Members.Count).Append(" operations\n");
                    text.Append("          ").Append(finding.Message).Append('\n');
                    var width = change.Members.Max(m => m.Operation.Length);
                    foreach (var member in change.Members)
                    {
                        text.Append("          - ").Append(member.Location == "operation"
                            ? member.Operation
                            : $"{member.Operation.PadRight(width)}  {member.Location}").Append('\n');
                    }

                    AppendPosition(text, finding, context);
                    continue;
                }

                text.Append("  [").Append(finding.Rule.Id).Append("] ").Append(finding.Operation).Append('\n');
                if (finding.Location != "operation")
                {
                    text.Append("          ").Append(finding.Location).Append('\n');
                }

                text.Append("          ").Append(finding.Message).Append('\n');
                AppendPosition(text, finding, context);
            }
        }

        if (result.Suppressed.Count > 0)
        {
            text.Append("\nSUPPRESSED (").Append(result.Suppressed.Count).Append(")\n");
            foreach (var (finding, suppression) in result.Suppressed)
            {
                text.Append("  [").Append(finding.Rule.Id).Append("] ").Append(finding.Operation).Append('\n');
                if (finding.Location != "operation")
                {
                    text.Append("          ").Append(finding.Location).Append('\n');
                }

                text.Append("          ").Append(finding.Message).Append('\n');
                AppendPosition(text, finding, context);
                text.Append("          Justification: ").Append(suppression.Justification).Append('\n');
            }
        }

        if (result.UnusedSuppressions.Count > 0)
        {
            text.Append("\nUNUSED SUPPRESSIONS (").Append(result.UnusedSuppressions.Count).Append(") - they match nothing or have expired; remove them from gapwatch.json, or rewrite the baseline\n");
            foreach (var suppression in result.UnusedSuppressions)
            {
                text.Append("  ").Append(suppression.Describe()).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// <c>at api/openapi.json:42:9</c> and <c>code at src/Pet.cs:12:5</c>, the compiler-style form editors and build logs
    /// turn into links.
    /// </summary>
    private static void AppendPosition(StringBuilder text, Finding finding, ReportContext context)
    {
        if (finding.Position is { } position)
        {
            text.Append("          at ").Append(finding.Document ?? context.HeadLabel).Append(':').Append(position).Append('\n');
        }

        if (finding.Code is { } code)
        {
            text.Append("          code at ").Append(code).Append('\n');
        }
    }
}

/// <summary>Markdown report suitable for pull request comments and wiki pages.</summary>
public sealed class MarkdownReportRenderer : IReportRenderer
{
    /// <summary>Hidden marker so CI can find and update a single "sticky" comment instead of posting a new one.</summary>
    public const string Marker = "<!-- gapwatch-report -->";

    /// <summary>
    /// The longest report, in characters. A GitHub pull request comment holds at most 65,536; rows past this budget are
    /// left out and counted, so the comment can still be posted.
    /// </summary>
    public const int MaxLength = 60_000;

    /// <summary>Shared-schema rows list their operations inline up to this count, and in a folded list beyond it.</summary>
    public const int InlineOperations = 5;

    /// <summary>Room kept for the closing note and sections after the budget is reached.</summary>
    private const int Reserve = 1_000;

    public string Render(ComparisonResult result, ReportContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var breaking = result.Count(Severity.Breaking);
        var warnings = result.Count(Severity.Warning);

        var text = new StringBuilder();
        text.Append(Marker).Append('\n');
        text.Append("## GapWatch API compatibility report\n\n");

        text.Append(breaking > 0
                ? $"❌ **Breaking changes detected** ({Summary.Describe(result)})"
                : warnings > 0
                    ? $"⚠️ **No breaking changes, but review the warnings** ({Summary.Describe(result)})"
                    : result.Suppressed.Count > 0
                        ? $"✅ **No breaking changes** ({Summary.Describe(result)})"
                        : "✅ **No breaking changes**")
            .Append("\n\n");

        text.Append('`').Append(Code(context.BaseLabel)).Append("` → `").Append(Code(context.HeadLabel)).Append("`\n");

        var omitted = 0;
        AppendGroup(text, result, Severity.Breaking, "❌", collapsed: false, ref omitted);
        AppendGroup(text, result, Severity.Warning, "⚠️", collapsed: false, ref omitted);
        AppendGroup(text, result, Severity.Info, "ℹ️", collapsed: true, ref omitted);
        AppendSuppressed(text, result, ref omitted);
        AppendUnused(text, result);

        if (omitted > 0)
        {
            text.Append("\n> [!WARNING]\n> ").Append(omitted.ToString(CultureInfo.InvariantCulture))
                .Append(" row(s) were left out to keep this comment under the size a pull request comment allows. ")
                .Append("The counts above include them; the SARIF and JUnit reports list every finding.\n");
        }

        return text.ToString();
    }

    /// <summary>Appends <paramref name="row"/> while the report stays within <see cref="MaxLength"/>; otherwise counts it.</summary>
    private static void AppendRow(StringBuilder text, string row, ref int omitted)
    {
        if (omitted == 0 && text.Length + row.Length <= MaxLength - Reserve)
        {
            text.Append(row);
        }
        else
        {
            omitted++;
        }
    }

    private static void AppendSuppressed(StringBuilder text, ComparisonResult result, ref int omitted)
    {
        if (result.Suppressed.Count == 0)
        {
            return;
        }

        text.Append("\n<details>\n<summary>🔕 Suppressed (").Append(result.Suppressed.Count).Append(")</summary>\n\n");
        text.Append("| Rule | Severity | Operation | Location | Details | Justification |\n|---|---|---|---|---|---|\n");
        foreach (var (finding, suppression) in result.Suppressed)
        {
            AppendRow(text, new StringBuilder().Append("| ").Append(finding.Rule.Id)
                .Append(" | ").Append(finding.Severity)
                .Append(" | `").Append(Code(finding.Operation))
                .Append("` | `").Append(Code(finding.Location)).Append('`').Append(Line(finding))
                .Append(" | ").Append(Cell(finding.Message))
                .Append(" | ").Append(Cell(suppression.Justification))
                .Append(" |\n").ToString(), ref omitted);
        }

        text.Append("\n</details>\n");
    }

    private static void AppendUnused(StringBuilder text, ComparisonResult result)
    {
        if (result.UnusedSuppressions.Count == 0)
        {
            return;
        }

        text.Append("\n> [!NOTE]\n> ").Append(result.UnusedSuppressions.Count)
            .Append(" suppression(s) matched nothing or have expired. Remove them from `gapwatch.json`, or rewrite the baseline for `baseline:` entries:\n");
        foreach (var suppression in result.UnusedSuppressions)
        {
            text.Append("> - `").Append(Code(suppression.Describe())).Append("`\n");
        }
    }

    private static void AppendGroup(StringBuilder text, ComparisonResult result, Severity severity, string icon, bool collapsed, ref int omitted)
    {
        var group = result.Findings.Where(f => f.Severity == severity).ToList();
        if (group.Count == 0)
        {
            return;
        }

        var title = $"{icon} {Summary.Heading(severity)} ({group.Count})";
        text.Append('\n');

        if (collapsed)
        {
            text.Append("<details>\n<summary>").Append(title).Append("</summary>\n\n");
        }
        else
        {
            text.Append("### ").Append(title).Append("\n\n");
        }

        text.Append("| Rule | Operation | Location | Details |\n|---|---|---|---|\n");
        foreach (var change in FindingGroup.Group(group))
        {
            var finding = change.First;
            var row = new StringBuilder();
            if (change.IsShared)
            {
                // One row per change: the component path is the cause, the operations are where consumers see it. An
                // operation that uses the schema in several responses (400 and 404) is listed once.
                var operations = change.Members.Select(m => m.Operation).Distinct(StringComparer.Ordinal).ToList();
                row.Append("| ").Append(finding.Rule.Id).Append(" | ").Append(OperationList(operations));
                if (finding.Origin is { } origin)
                {
                    row.Append(" | schema `").Append(Code(origin.Component)).Append("` `").Append(Code(origin.Path))
                        .Append("` (").Append(origin.Direction.ToString().ToLowerInvariant()).Append(", ");
                }
                else
                {
                    row.Append(" | API version `").Append(Code(finding.ApiVersion!)).Append("` (");
                }

                row.Append(operations.Count).Append(" operations)").Append(Line(finding))
                    .Append(" | ").Append(Cell(finding.Message))
                    .Append(" |\n");
            }
            else
            {
                row.Append("| ").Append(finding.Rule.Id)
                    .Append(" | `").Append(Code(finding.Operation))
                    .Append("` | `").Append(Code(finding.Location)).Append('`').Append(Line(finding))
                    .Append(" | ").Append(Cell(finding.Message))
                    .Append(" |\n");
            }

            AppendRow(text, row.ToString(), ref omitted);
        }

        if (collapsed)
        {
            text.Append("\n</details>\n");
        }
    }

    /// <summary>
    /// The operations of a shared change: inline up to <see cref="InlineOperations"/>, otherwise the first ones and the
    /// rest in a folded list, so one widely used schema does not fill the comment. On one line, as a table cell must be.
    /// </summary>
    private static string OperationList(IReadOnlyList<string> operations)
    {
        static string Item(string operation) => $"`{Code(operation)}`";

        if (operations.Count <= InlineOperations)
        {
            return string.Join("<br>", operations.Select(Item));
        }

        var shown = InlineOperations - 1;
        return string.Join("<br>", operations.Take(shown).Select(Item))
               + string.Create(CultureInfo.InvariantCulture, $"<details><summary>and {operations.Count - shown} more</summary>")
               + string.Join("<br>", operations.Skip(shown).Select(Item))
               + "</details>";
    }

    /// <summary>
    /// The head document line and the code location, under the location. A pull request comment cannot link to them
    /// reliably, because it does not know the commit.
    /// </summary>
    private static string Line(Finding finding)
    {
        var line = finding.Position is { } position
            ? string.Create(CultureInfo.InvariantCulture, $"<br>line {position.Line}")
            : string.Empty;
        return finding.Code is { } code
            ? string.Create(CultureInfo.InvariantCulture, $"{line}<br>`{Code(code.File)}:{code.Line}`")
            : line;
    }

    private static string Code(string value) => value.Replace('`', '\'').Replace("|", "\\|", StringComparison.Ordinal);

    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>Renders the rule catalog (used by <c>gapwatch rules</c> and to generate documentation).</summary>
public static class RuleListRenderer
{
    /// <summary>Aligned plain-text list. A plugin rule names its plugin after the rule name.</summary>
    /// <param name="rules">The rules to list; null for the built-in rules alone.</param>
    public static string RenderConsole(RuleSet? rules = null)
    {
        rules ??= RuleSet.BuiltIn;
        var width = rules.All.Max(r => r.Id.Length);
        var text = new StringBuilder();
        foreach (var rule in rules.All)
        {
            text.Append(rule.Id.PadRight(width)).Append("  ")
                .Append(rule.DefaultSeverity.ToString().ToUpperInvariant().PadRight(8)).Append("  ")
                .Append(rule.Name);
            if (rules.PluginOf(rule.Id) is { } plugin)
            {
                text.Append(" (plugin ").Append(plugin).Append(')');
            }

            text.Append('\n')
                .Append(new string(' ', width + 2)).Append(rule.Description).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Markdown table.</summary>
    /// <param name="rules">The rules to list; null for the built-in rules alone, as in <c>docs/rules.md</c>.</param>
    public static string RenderMarkdown(RuleSet? rules = null)
    {
        var text = new StringBuilder();
        text.Append("| Rule | Severity | Name | What it detects |\n|---|---|---|---|\n");
        foreach (var rule in (rules ?? RuleSet.BuiltIn).All)
        {
            text.Append("| ").Append(rule.Id)
                .Append(" | ").Append(rule.DefaultSeverity)
                .Append(" | ").Append(rule.Name)
                .Append(" | ").Append(rule.Description.Replace("|", "\\|", StringComparison.Ordinal))
                .Append(" |\n");
        }

        return text.ToString();
    }
}

internal static class Summary
{
    public static string Heading(Severity severity) => severity switch
    {
        Severity.Breaking => "Breaking",
        Severity.Warning => "Warnings",
        _ => "Info",
    };

    public static string Describe(ComparisonResult result)
    {
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{result.Count(Severity.Breaking)} breaking, {result.Count(Severity.Warning)} warning(s), {result.Count(Severity.Info)} info");

        return result.Suppressed.Count == 0
            ? summary
            : string.Create(CultureInfo.InvariantCulture, $"{summary}, {result.Suppressed.Count} suppressed");
    }
}
