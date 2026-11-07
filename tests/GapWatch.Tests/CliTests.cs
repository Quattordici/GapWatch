using System.Text.RegularExpressions;
using GapWatch.Cli;
using GapWatch.Core.Findings;
using Shouldly;

namespace GapWatch.Tests;

public sealed class CliTests
{
    private static readonly string V1 = TestSpec.FixturePath("petstore-v1.json");
    private static readonly string V2 = TestSpec.FixturePath("petstore-v2.json");

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = GapWatchCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Test]
    public void Compare_with_breaking_changes_exits_with_findings_code_and_prints_the_report()
    {
        var (code, output, _) = Run("compare", V1, V2);

        code.ShouldBe(ExitCodes.FindingsAtThreshold);
        output.ShouldContain("BREAKING (");
        output.ShouldContain("GW001");
    }

    [Test]
    public void Compare_with_source_maps_findings_to_the_csharp_code()
    {
        var (code, output, _) = Run("compare", V1, V2, "--source", TestSpec.FixturePath("PetstoreApi"));

        code.ShouldBe(ExitCodes.FindingsAtThreshold);
        output.ShouldContain("code at " + Path.Join(TestSpec.FixturePath("PetstoreApi"), "Models.cs").Replace('\\', '/') + ":16:12\n");
    }

    [Test]
    public void Compare_with_a_missing_source_directory_is_a_usage_error()
    {
        var (code, _, error) = Run("compare", V1, V2, "--source", "/definitely/not/a/source/dir");

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("Source directory not found");
    }

    [Test]
    public void Compare_of_identical_files_succeeds()
    {
        var (code, output, _) = Run("compare", V1, V1);

        code.ShouldBe(ExitCodes.Success);
        output.ShouldContain("No differences that affect consumers");
    }

    [Test]
    public void Fail_on_none_never_fails_on_findings()
    {
        Run("compare", V1, V2, "--fail-on", "none").Code.ShouldBe(ExitCodes.Success);
    }

    [Test]
    public void Fail_on_warning_fails_on_warnings_but_the_default_does_not()
    {
        using var dir = new TempDir();
        var before = dir.Write("before.json", TestSpec.Parameter("q", "query", """{ "type": "string" }"""));
        var after = dir.Write("after.json", TestSpec.Operation("get", "/x"));

        Run("compare", before, after).Code.ShouldBe(ExitCodes.Success);
        Run("compare", before, after, "--fail-on", "warning").Code.ShouldBe(ExitCodes.FindingsAtThreshold);
    }

    [Test]
    public void Markdown_format_is_selectable_with_both_option_styles()
    {
        Run("compare", V1, V2, "--format", "markdown").Out.ShouldContain(GapWatch.Reporting.MarkdownReportRenderer.Marker);
        Run("compare", V1, V2, "--format=markdown").Out.ShouldContain(GapWatch.Reporting.MarkdownReportRenderer.Marker);
    }

    [Test]
    public void Output_option_writes_the_report_to_a_file_and_summarizes_on_stdout()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "report.md");

        var (code, output, _) = Run("compare", V1, V2, "--format", "markdown", "--output", target);

        code.ShouldBe(ExitCodes.FindingsAtThreshold);
        File.ReadAllText(target).ShouldContain("## GapWatch API compatibility report");
        output.ShouldContain("Report written to");
        output.ShouldNotContain("## GapWatch");
    }

    [Test]
    public void Config_can_raise_a_rule_to_breaking()
    {
        using var dir = new TempDir();
        var before = dir.Write("before.json", TestSpec.Parameter("q", "query", """{ "type": "string" }"""));
        var after = dir.Write("after.json", TestSpec.Operation("get", "/x"));
        var config = dir.Write("gapwatch.json", """{ "rules": { "GW012": "breaking" } }""");

        Run("compare", before, after, "--config", config).Code.ShouldBe(ExitCodes.FindingsAtThreshold);
    }

    [Test]
    public void Config_suppressions_take_findings_out_of_the_exit_code_and_show_the_justification()
    {
        using var dir = new TempDir();
        var before = dir.Write("before.json", TestSpec.Operation("get", "/x"));
        var after = dir.Write("after.json", TestSpec.Operation("get", "/y"));
        var config = dir.Write(
            "gapwatch.json",
            """{ "suppressions": [ { "rule": "GW001", "operation": "GET /x", "justification": "Retired, no traffic for 90 days." } ] }""");

        Run("compare", before, after).Code.ShouldBe(ExitCodes.FindingsAtThreshold);

        var (code, output, _) = Run("compare", before, after, "--config", config);

        code.ShouldBe(ExitCodes.Success);
        output.ShouldContain("0 breaking, 0 warning(s), 1 info, 1 suppressed");
        output.ShouldContain("SUPPRESSED (1)");
        output.ShouldContain("Justification: Retired, no traffic for 90 days.");
    }

    [Test]
    public void Invalid_or_missing_config_is_an_error()
    {
        using var dir = new TempDir();
        var unjustified = dir.Write("gapwatch.json", """{ "suppressions": [ { "rule": "GW001" } ] }""");

        var (code, output, error) = Run("compare", V1, V2, "--config", unjustified);

        code.ShouldBe(ExitCodes.Error);
        output.ShouldBeEmpty();
        error.ShouldContain("has no justification");
        Run("compare", V1, V2, "--config", "/definitely/not/gapwatch.json").Code.ShouldBe(ExitCodes.Error);
    }

    [Test]
    public void Sarif_and_junit_formats_are_selectable_and_keep_the_exit_code()
    {
        var sarif = Run("compare", V1, V2, "--format", "sarif");
        var junit = Run("compare", V1, V2, "--format", "junit", "--fail-on", "none");

        sarif.Code.ShouldBe(ExitCodes.FindingsAtThreshold);
        sarif.Out.ShouldContain("\"version\": \"2.1.0\"");
        sarif.Out.ShouldContain("\"semanticVersion\"");
        junit.Code.ShouldBe(ExitCodes.Success);
        junit.Out.ShouldContain("<testsuites name=\"GapWatch\"");
        junit.Out.ShouldContain("failures=\"0\"");
    }

    [Test]
    public void Rules_does_not_accept_report_only_formats()
    {
        Run("rules", "--format", "sarif").Code.ShouldBe(ExitCodes.Error);
    }

    [Test]
    public void No_arguments_is_a_usage_error()
    {
        var (code, _, error) = Run();

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("Usage:");
    }

    [Test]
    public void Unknown_commands_and_options_are_usage_errors()
    {
        Run("frobnicate").Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, "--bogus", "x").Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, "--format", "pdf").Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, "--fail-on", "sometimes").Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, "--format").Code.ShouldBe(ExitCodes.Error);
    }

    [Test]
    public void Compare_requires_exactly_two_inputs()
    {
        Run("compare", V1).Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, V2).Code.ShouldBe(ExitCodes.Error);
    }

    [Test]
    public void Missing_files_are_reported_on_stderr_with_the_error_code()
    {
        var (code, output, error) = Run("compare", V1, "/definitely/not/here.json");

        code.ShouldBe(ExitCodes.Error);
        output.ShouldBeEmpty();
        error.ShouldContain("not found");
    }

    [Test]
    public void Invalid_documents_are_reported_not_thrown()
    {
        using var dir = new TempDir();
        var broken = dir.Write("broken.json", "{ nope");

        var (code, _, error) = Run("compare", V1, broken);

        code.ShouldBe(ExitCodes.Error);
        error.ShouldStartWith("error:");
    }

    [Test]
    public void Rules_lists_every_rule_in_both_formats()
    {
        var console = Run("rules");
        var markdown = Run("rules", "--format", "markdown");

        console.Code.ShouldBe(ExitCodes.Success);
        markdown.Out.ShouldContain("| Rule | Severity | Name |");
        foreach (var rule in RuleCatalog.All)
        {
            console.Out.ShouldContain(rule.Id);
            markdown.Out.ShouldContain(rule.Id);
        }
    }

    [Test]
    public void Help_and_version_succeed()
    {
        Run("--help").Out.ShouldContain("gapwatch compare");
        Regex.IsMatch(Run("--version").Out.Trim(), @"^\d+\.\d+\.\d+").ShouldBeTrue();
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gapwatch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
