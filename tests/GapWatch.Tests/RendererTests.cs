using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Policy;
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

    [Test]
    public void Console_report_without_findings_says_so()
    {
        var report = new ConsoleReportRenderer().Render(new ComparisonResult([]), Context);

        report.ShouldContain("0 breaking");
        report.ShouldContain("No differences that affect consumers");
    }

    [Test]
    public void Console_report_orders_groups_from_most_to_least_severe()
    {
        var report = new ConsoleReportRenderer().Render(PetstoreDiff(), Context);

        var breaking = report.IndexOf("BREAKING (", StringComparison.Ordinal);
        var warnings = report.IndexOf("WARNINGS (", StringComparison.Ordinal);
        var info = report.IndexOf("INFO (", StringComparison.Ordinal);

        (breaking >= 0 && breaking < warnings && warnings < info).ShouldBeTrue();
    }

    [Test]
    public void Markdown_report_without_findings_is_green_and_carries_the_marker()
    {
        var report = new MarkdownReportRenderer().Render(new ComparisonResult([]), Context);

        report.ShouldStartWith(MarkdownReportRenderer.Marker);
        report.ShouldContain("✅ **No breaking changes**");
    }

    [Test]
    public void Markdown_report_escapes_table_syntax_and_html_in_messages()
    {
        var finding = new Finding(RuleCatalog.TypeChanged, "GET /x", "loc|x", "a|b <script>");

        var report = new MarkdownReportRenderer().Render(new ComparisonResult([finding]), Context);

        report.ShouldContain("a\\|b &lt;script&gt;");
        report.ShouldContain("`loc\\|x`");
        report.ShouldNotContain("<script>");
    }

    [Test]
    public void Markdown_report_for_warnings_only_is_flagged_for_review()
    {
        var finding = new Finding(RuleCatalog.ParameterRemoved, "GET /x", "parameter 'q' (query)", "Parameter 'q' (query) was removed.");

        var report = new MarkdownReportRenderer().Render(new ComparisonResult([finding]), Context);

        report.ShouldContain("⚠️ **No breaking changes, but review the warnings**");
    }

    [Test]
    public void Markdown_report_collapses_informational_findings()
    {
        var report = new MarkdownReportRenderer().Render(PetstoreDiff(), Context);

        report.ShouldContain("<details>");
        report.ShouldContain("</details>");
    }

    private static ComparisonResult WithPolicy()
    {
        var policy = new CompatibilityPolicy(
            new Dictionary<string, Severity>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            [
                new Suppression("GW001", "GET /owners", null, "Owners moved to the CRM | agreed in <ADR-7>."),
                new Suppression("GW030", "GET /stale", null, "Left over."),
            ]);

        return policy.Apply(PetstoreDiff());
    }

    [Test]
    public void Console_report_lists_suppressed_findings_with_justification_and_unused_suppressions()
    {
        var report = new ConsoleReportRenderer().Render(WithPolicy(), Context);

        report.ShouldContain("19 breaking, 7 warning(s), 3 info, 1 suppressed");
        report.ShouldContain("SUPPRESSED (1)\n  [GW001] GET /owners\n");
        report.ShouldContain("Justification: Owners moved to the CRM | agreed in <ADR-7>.");
        report.ShouldContain("UNUSED SUPPRESSIONS (1)");
        report.ShouldContain("  GW030 GET /stale\n");
    }

    [Test]
    public void Console_report_with_everything_suppressed_says_so()
    {
        var finding = new Finding(RuleCatalog.OperationRemoved, "GET /x", "operation", "Operation 'GET /x' was removed.");
        var result = new ComparisonResult([], [new SuppressedFinding(finding, new Suppression("GW001", null, null, "ok"))]);

        new ConsoleReportRenderer().Render(result, Context).ShouldContain("No unsuppressed differences");
    }

    /// <summary>A change in schema <c>Pet $.name</c> as seen by each of <paramref name="operations"/>.</summary>
    private static List<Finding> SharedChange(params string[] operations) =>
        operations.Select(o => new Finding(RuleCatalog.TypeChanged, o, $"response 200 application/json: $.name ({o})", "Type changed.")
        {
            Origin = new SchemaOrigin("Pet", "$.name", Direction.Response),
        }).ToList();

    [Test]
    public void Markdown_report_lists_each_operation_of_a_shared_change_once_and_folds_long_lists()
    {
        var shortList = new MarkdownReportRenderer().Render(new ComparisonResult(SharedChange("GET /a", "GET /a", "GET /b")), Context);
        var longList = new MarkdownReportRenderer().Render(
            new ComparisonResult(SharedChange("GET /1", "GET /2", "GET /3", "GET /4", "GET /5", "GET /6")), Context);

        shortList.ShouldContain("| GW020 | `GET /a`<br>`GET /b` | schema `Pet` `$.name` (response, 2 operations)");
        longList.ShouldContain("`GET /3`<br>`GET /4`<details><summary>and 2 more</summary>`GET /5`<br>`GET /6`</details> |");
    }

    [Test]
    public void Markdown_report_stays_within_a_pull_request_comment_and_says_what_it_left_out()
    {
        var findings = Enumerable.Range(0, 2_000)
            .Select(i => new Finding(RuleCatalog.OperationRemoved, $"GET /pets/{i}", "operation", $"Operation 'GET /pets/{i}' was removed."))
            .ToList();

        var report = new MarkdownReportRenderer().Render(new ComparisonResult(findings), Context);

        (report.Length <= MarkdownReportRenderer.MaxLength).ShouldBeTrue();
        report.ShouldContain("(2000 breaking, 0 warning(s), 0 info)");
        report.ShouldContain("row(s) were left out to keep this comment under the size a pull request comment allows.");
        report.ShouldContain("`GET /pets/0`");
        report.ShouldNotContain("`GET /pets/1999`");
    }

    [Test]
    public void Markdown_report_collapses_suppressed_findings_and_escapes_the_justification()
    {
        var report = new MarkdownReportRenderer().Render(WithPolicy(), Context);

        report.ShouldContain("<summary>🔕 Suppressed (1)</summary>");
        report.ShouldContain("| GW001 | Breaking | `GET /owners` | `operation` |");
        report.ShouldContain("Owners moved to the CRM \\| agreed in &lt;ADR-7&gt;.");
        report.ShouldContain("> 1 suppression(s) matched nothing");
        report.ShouldContain("> - `GW030 GET /stale`");
    }

    [Test]
    public void Markdown_report_is_green_when_every_breaking_change_is_suppressed()
    {
        var finding = new Finding(RuleCatalog.OperationRemoved, "GET /x", "operation", "Operation 'GET /x' was removed.");
        var result = new ComparisonResult([], [new SuppressedFinding(finding, new Suppression("GW001", null, null, "ok"))]);

        new MarkdownReportRenderer().Render(result, Context).ShouldContain("✅ **No breaking changes** (0 breaking, 0 warning(s), 0 info, 1 suppressed)");
    }

    [Test]
    public void Petstore_markdown_report_matches_the_golden_file() =>
        Golden.Verify("petstore.md", new MarkdownReportRenderer().Render(PetstoreDiff(), Context));

    [Test]
    public void Petstore_console_report_matches_the_golden_file() =>
        Golden.Verify("petstore.txt", new ConsoleReportRenderer().Render(PetstoreDiff(), Context));

    [Test]
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

    [Test]
    public void Reports_are_deterministic()
    {
        var first = new MarkdownReportRenderer().Render(PetstoreDiff(), Context);
        var second = new MarkdownReportRenderer().Render(PetstoreDiff(), Context);

        second.ShouldBe(first);
    }
}
