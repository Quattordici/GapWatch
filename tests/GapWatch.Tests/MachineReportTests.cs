using System.Text.Json;
using System.Xml.Linq;
using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Policy;
using GapWatch.OpenApi;
using GapWatch.Reporting;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>SARIF and JUnit output: structure that consumers (code scanning, test tabs) rely on.</summary>
public sealed class MachineReportTests
{
    private static readonly ReportContext Context = new("petstore-v1.json", "petstore-v2.json");

    private static readonly Finding Removed =
        new(RuleCatalog.OperationRemoved, "GET /owners", "operation", "Operation 'GET /owners' was removed.");

    private static readonly Finding ParameterGone =
        new(RuleCatalog.ParameterRemoved, "GET /pets", "parameter 'q' (query)", "Parameter 'q' (query) was removed.");

    private static ComparisonResult PetstoreDiff() =>
        new ContractComparer().Compare(
            ContractLoader.Load(TestSpec.FixturePath("petstore-v1.json")),
            ContractLoader.Load(TestSpec.FixturePath("petstore-v2.json")));

    private static ComparisonResult Suppressed() =>
        new(
            [ParameterGone],
            [new SuppressedFinding(Removed, new Suppression("GW001", "GET /owners", null, "Moved to the CRM."))],
            [new Suppression("GW030", "GET /stale", null, "Left over.")]);

    private static JsonElement Run(string sarif) => JsonDocument.Parse(sarif).RootElement.GetProperty("runs")[0];

    // --- SARIF ---------------------------------------------------------------------------------------------------

    [Test]
    public void Sarif_is_version_2_1_0_and_lists_every_rule_with_its_default_level()
    {
        var root = JsonDocument.Parse(new SarifReportRenderer().Render(new ComparisonResult([]), Context)).RootElement;

        root.GetProperty("version").GetString().ShouldBe("2.1.0");
        var rules = Run(root.GetRawText()).GetProperty("tool").GetProperty("driver").GetProperty("rules");
        rules.GetArrayLength().ShouldBe(RuleCatalog.All.Count);
        rules[0].GetProperty("id").GetString().ShouldBe("GW001");
        rules[0].GetProperty("defaultConfiguration").GetProperty("level").GetString().ShouldBe("error");
        rules[0].GetProperty("help").GetProperty("text").GetString()!.ShouldStartWith(RuleCatalog.OperationRemoved.Description);
        Run(root.GetRawText()).GetProperty("results").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public void Sarif_maps_severities_to_levels()
    {
        SarifReportRenderer.Level(Severity.Breaking).ShouldBe("error");
        SarifReportRenderer.Level(Severity.Warning).ShouldBe("warning");
        SarifReportRenderer.Level(Severity.Info).ShouldBe("note");
    }

    [Test]
    public void Sarif_result_level_uses_the_effective_severity_and_the_rule_index_points_at_the_rule()
    {
        var overridden = ParameterGone with { Severity = Severity.Breaking };

        var run = Run(new SarifReportRenderer().Render(new ComparisonResult([overridden]), Context));
        var result = run.GetProperty("results")[0];

        result.GetProperty("ruleId").GetString().ShouldBe("GW012");
        result.GetProperty("level").GetString().ShouldBe("error");
        var index = result.GetProperty("ruleIndex").GetInt32();
        run.GetProperty("tool").GetProperty("driver").GetProperty("rules")[index].GetProperty("id").GetString().ShouldBe("GW012");
    }

    [Test]
    public void Sarif_results_point_at_the_head_document_and_carry_the_logical_location()
    {
        var result = Run(new SarifReportRenderer().Render(new ComparisonResult([ParameterGone]), Context)).GetProperty("results")[0];
        var location = result.GetProperty("locations")[0];

        location.GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString().ShouldBe("petstore-v2.json");
        var region = location.GetProperty("physicalLocation").GetProperty("region");
        $"{region.GetProperty("startLine")}:{region.GetProperty("startColumn")}-{region.GetProperty("endLine")}:{region.GetProperty("endColumn")}"
            .ShouldBe("1:1-1:2");
        location.GetProperty("logicalLocations")[0].GetProperty("fullyQualifiedName").GetString().ShouldBe("GET /pets > parameter 'q' (query)");
        result.GetProperty("message").GetProperty("text").GetString().ShouldBe("GET /pets: Parameter 'q' (query) was removed.");
    }

    [Test]
    public void Sarif_reports_suppressed_findings_as_external_suppressions_and_unused_ones_as_notifications()
    {
        var run = Run(new SarifReportRenderer().Render(Suppressed(), Context));
        var results = run.GetProperty("results");

        results.GetArrayLength().ShouldBe(2);
        results[0].TryGetProperty("suppressions", out _).ShouldBeFalse();
        var suppression = results[1].GetProperty("suppressions")[0];
        suppression.GetProperty("kind").GetString().ShouldBe("external");
        suppression.GetProperty("status").GetString().ShouldBe("accepted");
        suppression.GetProperty("justification").GetString().ShouldBe("Moved to the CRM.");

        var notification = run.GetProperty("invocations")[0].GetProperty("toolExecutionNotifications")[0];
        notification.GetProperty("message").GetProperty("text").GetString()!.ShouldContain("GW030 GET /stale");
    }

    [Test]
    public void Sarif_fingerprints_ignore_the_message_but_not_the_location()
    {
        var reworded = ParameterGone with { Message = "Different words." };
        var elsewhere = ParameterGone with { Location = "parameter 'r' (query)" };

        SarifReportRenderer.Fingerprint(reworded).ShouldBe(SarifReportRenderer.Fingerprint(ParameterGone));
        SarifReportRenderer.Fingerprint(elsewhere).ShouldNotBe(SarifReportRenderer.Fingerprint(ParameterGone));
        SarifReportRenderer.Fingerprint(ParameterGone).Length.ShouldBe(64);
    }

    [Test]
    public void Sarif_artifact_uris_use_forward_slashes_and_percent_encoding()
    {
        SarifReportRenderer.ArtifactUri("api\\v1 spec.json").ShouldBe("api/v1%20spec.json");
        SarifReportRenderer.ArtifactUri("specs/openapi.json").ShouldBe("specs/openapi.json");
        SarifReportRenderer.ArtifactUri(Path.Combine(Path.GetTempPath(), "a.json")).ShouldStartWith("file:///");
    }

    [Test]
    public void Sarif_records_the_tool_version_only_when_known()
    {
        var withVersion = Run(new SarifReportRenderer().Render(new ComparisonResult([]), Context with { ToolVersion = "0.2.0" }));
        var without = Run(new SarifReportRenderer().Render(new ComparisonResult([]), Context));

        withVersion.GetProperty("tool").GetProperty("driver").GetProperty("semanticVersion").GetString().ShouldBe("0.2.0");
        without.GetProperty("tool").GetProperty("driver").TryGetProperty("semanticVersion", out _).ShouldBeFalse();
    }

    [Test]
    public void Petstore_sarif_report_matches_the_golden_file() =>
        Golden.Verify("petstore.sarif", new SarifReportRenderer().Render(PetstoreDiff(), Context));

    // --- JUnit ---------------------------------------------------------------------------------------------------

    private static XElement Suite(string xml) => XDocument.Parse(xml).Root!.Element("testsuite")!;

    [Test]
    public void Junit_marks_findings_at_or_above_the_threshold_as_failures()
    {
        var result = new ComparisonResult(FindingOrder.Sort([Removed, ParameterGone]));

        var suite = Suite(new JUnitReportRenderer().Render(result, Context));

        suite.Attribute("tests")!.Value.ShouldBe("2");
        suite.Attribute("failures")!.Value.ShouldBe("1");
        var cases = suite.Elements("testcase").ToList();
        cases[0].Attribute("classname")!.Value.ShouldBe("GET /owners");
        cases[0].Attribute("name")!.Value.ShouldBe("GW001 OperationRemoved: operation");
        cases[0].Element("failure")!.Attribute("message")!.Value.ShouldBe("Operation 'GET /owners' was removed.");
        cases[1].Element("failure").ShouldBeNull();
        cases[1].Element("system-out")!.Value.ShouldStartWith("Warning: GET /pets parameter 'q' (query)");
    }

    [Test]
    public void Junit_follows_the_fail_on_threshold()
    {
        var result = new ComparisonResult(FindingOrder.Sort([Removed, ParameterGone]));

        Suite(new JUnitReportRenderer().Render(result, Context with { FailOn = Severity.Warning })).Attribute("failures")!.Value.ShouldBe("2");
        Suite(new JUnitReportRenderer().Render(result, Context with { FailOn = null })).Attribute("failures")!.Value.ShouldBe("0");
    }

    [Test]
    public void Junit_reports_suppressed_findings_as_skipped_with_the_justification()
    {
        var suite = Suite(new JUnitReportRenderer().Render(Suppressed(), Context));

        suite.Attribute("skipped")!.Value.ShouldBe("1");
        suite.Attribute("failures")!.Value.ShouldBe("0");
        suite.Elements("testcase").Single(c => c.Element("skipped") is not null)
            .Element("skipped")!.Attribute("message")!.Value.ShouldBe("Suppressed: Moved to the CRM.");
    }

    [Test]
    public void Junit_without_findings_still_has_one_passing_test()
    {
        var suite = Suite(new JUnitReportRenderer().Render(new ComparisonResult([]), Context));

        suite.Attribute("tests")!.Value.ShouldBe("1");
        suite.Attribute("failures")!.Value.ShouldBe("0");
    }

    [Test]
    public void Junit_escapes_xml_in_messages()
    {
        var finding = new Finding(RuleCatalog.TypeChanged, "GET /x", "loc", "a <b> & \"c\"");

        var xml = new JUnitReportRenderer().Render(new ComparisonResult([finding]), Context);

        Suite(xml).Element("testcase")!.Element("failure")!.Attribute("message")!.Value.ShouldBe("a <b> & \"c\"");
    }

    [Test]
    public void Petstore_junit_report_matches_the_golden_file() =>
        Golden.Verify("petstore.junit.xml", new JUnitReportRenderer().Render(PetstoreDiff(), Context));
}
