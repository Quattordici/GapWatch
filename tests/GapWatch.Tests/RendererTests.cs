using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.OpenApi;
using GapWatch.Reporting;
using Shouldly;

namespace GapWatch.Tests;

public sealed class RendererTests
{
    private static readonly ReportContext Context = new("petstore-v1.json", "petstore-v2.json");

    private static ComparisonResult PetstoreDiff() =>
        new ContractComparer().Compare(
            ContractLoader.Load(TestSpec.FixturePath("petstore-v1.json")),
            ContractLoader.Load(TestSpec.FixturePath("petstore-v2.json")));

    [Fact]
    public void Console_report_without_findings_says_so()
    {
        var report = new ConsoleReportRenderer().Render(new ComparisonResult([]), Context);

        report.ShouldContain("0 breaking");
        report.ShouldContain("No differences that affect consumers");
    }

    [Fact]
    public void Console_report_orders_groups_from_most_to_least_severe()
    {
        var report = new ConsoleReportRenderer().Render(PetstoreDiff(), Context);

        var breaking = report.IndexOf("BREAKING (", StringComparison.Ordinal);
        var warnings = report.IndexOf("WARNINGS (", StringComparison.Ordinal);
        var info = report.IndexOf("INFO (", StringComparison.Ordinal);

        (breaking >= 0 && breaking < warnings && warnings < info).ShouldBeTrue();
    }

    [Fact]
    public void Markdown_report_without_findings_is_green_and_carries_the_marker()
    {
        var report = new MarkdownReportRenderer().Render(new ComparisonResult([]), Context);

        report.ShouldStartWith(MarkdownReportRenderer.Marker);
        report.ShouldContain("✅ **No breaking changes**");
    }

    [Fact]
    public void Markdown_report_escapes_table_syntax_and_html_in_messages()
    {
        var finding = new Finding(RuleCatalog.TypeChanged, "GET /x", "loc|x", "a|b <script>");

        var report = new MarkdownReportRenderer().Render(new ComparisonResult([finding]), Context);

        report.ShouldContain("a\\|b &lt;script&gt;");
        report.ShouldContain("`loc\\|x`");
        report.ShouldNotContain("<script>");
    }

    [Fact]
    public void Markdown_report_for_warnings_only_is_flagged_for_review()
    {
        var finding = new Finding(RuleCatalog.ParameterRemoved, "GET /x", "parameter 'q' (query)", "Parameter 'q' (query) was removed.");

        var report = new MarkdownReportRenderer().Render(new ComparisonResult([finding]), Context);

        report.ShouldContain("⚠️ **No breaking changes, but review the warnings**");
    }

    [Fact]
    public void Markdown_report_collapses_informational_findings()
    {
        var report = new MarkdownReportRenderer().Render(PetstoreDiff(), Context);

        report.ShouldContain("<details>");
        report.ShouldContain("</details>");
    }

    [Fact]
    public void Petstore_markdown_report_matches_the_golden_file() =>
        Golden.Verify("petstore.md", new MarkdownReportRenderer().Render(PetstoreDiff(), Context));

    [Fact]
    public void Petstore_console_report_matches_the_golden_file() =>
        Golden.Verify("petstore.txt", new ConsoleReportRenderer().Render(PetstoreDiff(), Context));

    [Fact]
    public void Rule_list_mentions_every_rule_in_both_formats()
    {
        var console = RuleListRenderer.RenderConsole();
        var markdown = RuleListRenderer.RenderMarkdown();

        foreach (var rule in RuleCatalog.All)
        {
            console.ShouldContain(rule.Id);
            markdown.ShouldContain(rule.Id);
        }
    }

    [Fact]
    public void Reports_are_deterministic()
    {
        var first = new MarkdownReportRenderer().Render(PetstoreDiff(), Context);
        var second = new MarkdownReportRenderer().Render(PetstoreDiff(), Context);

        second.ShouldBe(first);
    }
}
