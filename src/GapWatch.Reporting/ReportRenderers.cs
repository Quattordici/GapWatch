using System.Globalization;
using System.Text;
using GapWatch.Core.Findings;

namespace GapWatch.Reporting;

/// <summary>Labels shown in a report header.</summary>
/// <param name="BaseLabel">Where the baseline came from (usually a file name).</param>
/// <param name="HeadLabel">Where the proposed contract came from.</param>
public sealed record ReportContext(string BaseLabel, string HeadLabel);

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
            text.Append("\nNo differences that affect consumers were found.\n");
            return text.ToString();
        }

        foreach (var severity in new[] { Severity.Breaking, Severity.Warning, Severity.Info })
        {
            var group = result.Findings.Where(f => f.Severity == severity).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            text.Append('\n').Append(Summary.Heading(severity).ToUpperInvariant()).Append(" (").Append(group.Count).Append(")\n");
            foreach (var finding in group)
            {
                text.Append("  [").Append(finding.Rule.Id).Append("] ").Append(finding.Operation).Append('\n');
                if (finding.Location != "operation")
                {
                    text.Append("          ").Append(finding.Location).Append('\n');
                }

                text.Append("          ").Append(finding.Message).Append('\n');
            }
        }

        return text.ToString();
    }
}

/// <summary>Markdown report suitable for pull request comments and wiki pages.</summary>
public sealed class MarkdownReportRenderer : IReportRenderer
{
    /// <summary>Hidden marker so CI can find and update a single "sticky" comment instead of posting a new one.</summary>
    public const string Marker = "<!-- gapwatch-report -->";

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
                    : "✅ **No breaking changes**")
            .Append("\n\n");

        text.Append('`').Append(Code(context.BaseLabel)).Append("` → `").Append(Code(context.HeadLabel)).Append("`\n");

        AppendGroup(text, result, Severity.Breaking, "❌", collapsed: false);
        AppendGroup(text, result, Severity.Warning, "⚠️", collapsed: false);
        AppendGroup(text, result, Severity.Info, "ℹ️", collapsed: true);

        return text.ToString();
    }

    private static void AppendGroup(StringBuilder text, ComparisonResult result, Severity severity, string icon, bool collapsed)
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
        foreach (var finding in group)
        {
            text.Append("| ").Append(finding.Rule.Id)
                .Append(" | `").Append(Code(finding.Operation))
                .Append("` | `").Append(Code(finding.Location))
                .Append("` | ").Append(Cell(finding.Message))
                .Append(" |\n");
        }

        if (collapsed)
        {
            text.Append("\n</details>\n");
        }
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
    /// <summary>Aligned plain-text list.</summary>
    public static string RenderConsole()
    {
        var text = new StringBuilder();
        foreach (var rule in RuleCatalog.All)
        {
            text.Append(rule.Id).Append("  ")
                .Append(rule.DefaultSeverity.ToString().ToUpperInvariant().PadRight(8)).Append("  ")
                .Append(rule.Name).Append('\n')
                .Append("       ").Append(rule.Description).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Markdown table.</summary>
    public static string RenderMarkdown()
    {
        var text = new StringBuilder();
        text.Append("| Rule | Severity | Name | What it detects |\n|---|---|---|---|\n");
        foreach (var rule in RuleCatalog.All)
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

    public static string Describe(ComparisonResult result) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{result.Count(Severity.Breaking)} breaking, {result.Count(Severity.Warning)} warning(s), {result.Count(Severity.Info)} info");
}
