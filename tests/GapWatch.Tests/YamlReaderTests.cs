using System.Text;
using System.Text.Json.Nodes;
using GapWatch.Core;
using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.OpenApi;
using GapWatch.Reporting;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>YAML documents go through the JSON reader, so these tests pin the YAML-to-JSON mapping.</summary>
public sealed class YamlReaderTests
{
    private static JsonNode Json(string yaml) =>
        OpenApiYamlReader.ToJson(new MemoryStream(Encoding.UTF8.GetBytes(yaml)));

    private static string Compact(string yaml) => Json(yaml).ToJsonString();

    private static string ErrorFor(string yaml) =>
        Should.Throw<ContractReadException>(() => Json(yaml)).Message;

    [Test]
    public void The_yaml_petstore_produces_exactly_the_same_report_as_the_json_petstore()
    {
        var json = new ContractComparer().Compare(
            ContractLoader.Load(TestSpec.FixturePath("petstore-v1.json")),
            ContractLoader.Load(TestSpec.FixturePath("petstore-v2.json")));
        var yaml = new ContractComparer().Compare(
            ContractLoader.Load(TestSpec.FixturePath("petstore-v1.yaml")),
            ContractLoader.Load(TestSpec.FixturePath("petstore-v2.yaml")));
        var context = new ReportContext("v1", "v2");

        // Positions differ by design (they point into each file); everything else must be identical.
        new ConsoleReportRenderer().Render(WithoutPositions(yaml), context)
            .ShouldBe(new ConsoleReportRenderer().Render(WithoutPositions(json), context));
    }

    private static ComparisonResult WithoutPositions(ComparisonResult result) =>
        new(result.Findings.Select(f => f with { Position = null }).ToList());

    [Test]
    public void Json_and_yaml_versions_can_be_compared_with_each_other()
    {
        var result = new ContractComparer().Compare(
            ContractLoader.Load(TestSpec.FixturePath("petstore-v1.json")),
            ContractLoader.Load(TestSpec.FixturePath("petstore-v1.yaml")));

        result.Findings.ShouldBeEmpty();
    }

    [Test]
    public void Plain_scalars_follow_the_yaml_1_2_core_schema()
    {
        Compact("a: 1\nb: -2\nc: 1.5\nd: 1e3\ne: 0x1F\nf: 0o17\ng: true\nh: False\ni: ~\nj:\nk: null\n")
            .ShouldBe("""{"a":1,"b":-2,"c":1.5,"d":1000,"e":31,"f":15,"g":true,"h":false,"i":null,"j":null,"k":null}""");
    }

    [Test]
    public void Yaml_1_1_booleans_and_version_like_values_stay_strings()
    {
        // YAML 1.1 parsers read yes/no/on/off as booleans; OpenAPI requires YAML 1.2, where they are strings.
        Compact("enum: [yes, no, on, off, Y, N]\nopenapi: 3.0.3\n")
            .ShouldBe("""{"enum":["yes","no","on","off","Y","N"],"openapi":"3.0.3"}""");
    }

    [Test]
    public void Quoted_and_str_tagged_scalars_are_always_strings()
    {
        Compact("a: '200'\nb: \"true\"\nc: !!str 42\nd: |\n  multi\n  line\n")
            .ShouldBe("""{"a":"200","b":"true","c":"42","d":"multi\nline\n"}""");
    }

    [Test]
    public void Numeric_mapping_keys_such_as_status_codes_become_strings()
    {
        Compact("responses:\n  200:\n    description: ok\n").ShouldBe("""{"responses":{"200":{"description":"ok"}}}""");
    }

    [Test]
    public void Anchors_and_aliases_are_expanded()
    {
        Compact("a: &x { type: string }\nb: *x\n").ShouldBe("""{"a":{"type":"string"},"b":{"type":"string"}}""");
    }

    [Test]
    public void Merge_keys_are_rejected_because_openapi_uses_yaml_1_2()
    {
        ErrorFor("base: &b { type: string }\nderived:\n  <<: *b\n").ShouldContain("merge keys");
    }

    [Test]
    public void Multi_document_streams_and_empty_documents_are_rejected()
    {
        ErrorFor("a: 1\n---\nb: 2\n").ShouldContain("exactly one YAML document, found 2");
        ErrorFor("").ShouldContain("found 0");
    }

    [Test]
    public void Invalid_yaml_is_reported_as_a_read_error_with_the_position_when_known()
    {
        ErrorFor("a: b: c\n").ShouldStartWith("The document is not valid YAML (line");
        ErrorFor("a: [1, 2\nb: 3\n").ShouldStartWith("The document is not valid YAML");
    }

    [Test]
    public void Duplicate_and_complex_keys_are_rejected()
    {
        ErrorFor("a: 1\na: 2\n").ShouldContain("uplicate");
        ErrorFor("? [a, b]\n: 1\n").ShouldContain("mapping keys must be plain strings");
    }

    [Test]
    public void The_cli_compares_yaml_files()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = GapWatch.Cli.GapWatchCli.Run(
            ["compare", TestSpec.FixturePath("petstore-v1.yaml"), TestSpec.FixturePath("petstore-v2.yaml")], output, error);

        code.ShouldBe(GapWatch.Cli.ExitCodes.FindingsAtThreshold);
        output.ToString().ShouldContain("20 breaking");
        error.ToString().ShouldBeEmpty();
    }
}
