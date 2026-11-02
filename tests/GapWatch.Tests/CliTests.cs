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

    [Fact]
    public void Compare_with_breaking_changes_exits_with_findings_code_and_prints_the_report()
    {
        var (code, output, _) = Run("compare", V1, V2);

        code.ShouldBe(ExitCodes.FindingsAtThreshold);
        output.ShouldContain("BREAKING (");
        output.ShouldContain("GW001");
    }

    [Fact]
    public void Compare_of_identical_files_succeeds()
    {
        var (code, output, _) = Run("compare", V1, V1);

        code.ShouldBe(ExitCodes.Success);
        output.ShouldContain("No differences that affect consumers");
    }

    [Fact]
    public void Fail_on_none_never_fails_on_findings()
    {
        Run("compare", V1, V2, "--fail-on", "none").Code.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public void Fail_on_warning_fails_on_warnings_but_the_default_does_not()
    {
        using var dir = new TempDir();
        var before = dir.Write("before.json", TestSpec.Parameter("q", "query", """{ "type": "string" }"""));
        var after = dir.Write("after.json", TestSpec.Operation("get", "/x"));

        Run("compare", before, after).Code.ShouldBe(ExitCodes.Success);
        Run("compare", before, after, "--fail-on", "warning").Code.ShouldBe(ExitCodes.FindingsAtThreshold);
    }

    [Fact]
    public void Markdown_format_is_selectable_with_both_option_styles()
    {
        Run("compare", V1, V2, "--format", "markdown").Out.ShouldContain(GapWatch.Reporting.MarkdownReportRenderer.Marker);
        Run("compare", V1, V2, "--format=markdown").Out.ShouldContain(GapWatch.Reporting.MarkdownReportRenderer.Marker);
    }

    [Fact]
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

    [Fact]
    public void No_arguments_is_a_usage_error()
    {
        var (code, _, error) = Run();

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("Usage:");
    }

    [Fact]
    public void Unknown_commands_and_options_are_usage_errors()
    {
        Run("frobnicate").Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, "--bogus", "x").Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, "--format", "pdf").Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, "--fail-on", "sometimes").Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, "--format").Code.ShouldBe(ExitCodes.Error);
    }

    [Fact]
    public void Compare_requires_exactly_two_inputs()
    {
        Run("compare", V1).Code.ShouldBe(ExitCodes.Error);
        Run("compare", V1, V2, V2).Code.ShouldBe(ExitCodes.Error);
    }

    [Fact]
    public void Missing_files_are_reported_on_stderr_with_the_error_code()
    {
        var (code, output, error) = Run("compare", V1, "/definitely/not/here.json");

        code.ShouldBe(ExitCodes.Error);
        output.ShouldBeEmpty();
        error.ShouldContain("not found");
    }

    [Fact]
    public void Invalid_documents_are_reported_not_thrown()
    {
        using var dir = new TempDir();
        var broken = dir.Write("broken.json", "{ nope");

        var (code, _, error) = Run("compare", V1, broken);

        code.ShouldBe(ExitCodes.Error);
        error.ShouldStartWith("error:");
    }

    [Fact]
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

    [Fact]
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
