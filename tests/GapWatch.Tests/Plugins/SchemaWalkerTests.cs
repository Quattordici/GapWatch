using System.Text.Json;
using GapWatch.Core;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Core.Policy;
using GapWatch.Reporting;
using GapWatch.Sdk;
using Shouldly;

namespace GapWatch.Tests.Plugins;

public sealed class SchemaWalkerTests
{
    private static OperationPair Pair(string before, string after) =>
        new(TestSpec.Parse(before).Operations.Single(), TestSpec.Parse(after).Operations.Single());

    private static string Side(SchemaContract? schema) => schema is null ? "-" : "x";

    private static string Walk(OperationPair pair) =>
        string.Join(" | ", SchemaWalker.Walk(pair).Select(n => $"{n.Direction} {n.At} {Side(n.Before)}{Side(n.After)}"));

    [Test]
    public void A_response_is_walked_in_the_response_direction_with_missing_sides_as_null()
    {
        var pair = Pair(
            TestSpec.Response("""{ "type": "object", "properties": { "id": { "type": "string" }, "old": { "type": "string" } } }"""),
            TestSpec.Response("""{ "type": "object", "properties": { "id": { "type": "string" }, "tags": { "type": "array", "items": { "type": "string" } } } }"""));

        Walk(pair).ShouldBe(
            "Response response 200 application/json xx | Response response 200 application/json: $.id xx | " +
            "Response response 200 application/json: $.old x- | Response response 200 application/json: $.tags -x | " +
            "Response response 200 application/json: $.tags[] -x");
    }

    [Test]
    public void A_request_body_is_walked_in_the_request_direction_without_read_only_properties()
    {
        var schema = """{ "type": "object", "properties": { "id": { "type": "string", "readOnly": true }, "name": { "type": "string" } } }""";

        Walk(Pair(TestSpec.Request(schema), TestSpec.Request(schema))).ShouldBe(
            "Request request body (application/json) xx | Request request body (application/json): $.name xx");
    }

    [Test]
    public void Parameters_pair_like_the_built_in_rules_so_a_renamed_path_parameter_is_one_pair()
    {
        static string Path(string name) => TestSpec.Document(
            $$"""{ "/pets/{{{name}}}": { "get": { "parameters": [ { "name": "{{name}}", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "204": { "description": "d" } } } } }""");

        Walk(Pair(Path("id"), Path("petId"))).ShouldBe("Request parameter 'petId' (path) xx");
    }

    [Test]
    public void Alternatives_are_walked_by_component_name()
    {
        const string components = """{ "schemas": { "Cat": { "type": "object" }, "Dog": { "type": "object" }, "Fish": { "type": "object" } } }""";
        var pair = Pair(
            TestSpec.Response("""{ "oneOf": [ { "$ref": "#/components/schemas/Cat" }, { "$ref": "#/components/schemas/Dog" } ] }""", components: components),
            TestSpec.Response("""{ "oneOf": [ { "$ref": "#/components/schemas/Fish" }, { "$ref": "#/components/schemas/Cat" } ] }""", components: components));

        Walk(pair).ShouldBe(
            "Response response 200 application/json xx | Response response 200 application/json: $<Cat> xx | " +
            "Response response 200 application/json: $<Dog> x- | Response response 200 application/json: $<Fish> -x");
    }

    [Test]
    public void An_added_operation_is_walked_on_the_head_side_only()
    {
        var head = TestSpec.Parse(TestSpec.Parameter("limit", "query", """{ "type": "integer" }""")).Operations.Single();

        Walk(new OperationPair(null, head)).ShouldBe("Request parameter 'limit' (query) -x");
    }

    /// <summary>
    /// A direction-aware plugin rule built on the walker: a new <c>maxLength</c> on a string is breaking where consumers
    /// send it (they may send longer values) and harmless where they read it.
    /// </summary>
    private static readonly IRulePlugin MaxLength = new DelegatePlugin(
        "LEN", [new RuleDescriptor("LEN001", "MaxLengthAdded", Severity.Breaking, "A string got a maxLength where consumers send it.")],
        (pair, _) => SchemaWalker.Walk(pair)
            .Where(n => n is { Direction: Direction.Request, Before.MaxLength: null, After.MaxLength: not null })
            .Select(n => new PluginFinding("LEN001", n.At, $"A maxLength of {n.After!.MaxLength} was added.")));

    private static IReadOnlyList<Finding> Plugin(string before, string after) =>
        CompatibilityCheck.Run(TestSpec.Parse(before), TestSpec.Parse(after), GapWatchConfiguration.Default, rules: RuleSet.With([MaxLength]))
            .Result.Findings.Where(f => f.Rule.Id == "LEN001").ToList();

    [Test]
    public void A_direction_aware_plugin_rule_reports_the_request_side()
    {
        var finding = Plugin(
            TestSpec.Request("""{ "type": "object", "properties": { "name": { "type": "string" } } }"""),
            TestSpec.Request("""{ "type": "object", "properties": { "name": { "type": "string", "maxLength": 20 } } }""")).ShouldHaveSingleItem();

        finding.Location.ShouldBe("request body (application/json): $.name");
    }

    [Test]
    public void A_direction_aware_plugin_rule_ignores_the_safe_response_side()
    {
        Plugin(
            TestSpec.Response("""{ "type": "object", "properties": { "name": { "type": "string" } } }"""),
            TestSpec.Response("""{ "type": "object", "properties": { "name": { "type": "string", "maxLength": 20 } } }""")).ShouldBeEmpty();
    }

    [Test]
    public void Sarif_lists_plugin_rules_with_their_own_help_uri()
    {
        var result = CompatibilityCheck.Run(
            TestSpec.Parse(TestSpec.Document("{}")),
            TestSpec.Parse(TestSpec.Document($$"""{ "/x": { "post": { "responses": { "204": { "description": "d" } } } } }""")),
            GapWatchConfiguration.Default,
            rules: Acme.Rules()).Result;

        var sarif = new SarifReportRenderer().Render(result, new ReportContext("base.json", "head.json") { Rules = Acme.Rules() });
        var run = JsonDocument.Parse(sarif).RootElement.GetProperty("runs")[0];
        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray().ToList();
        var acme = rules.Last();

        acme.GetProperty("id").GetString().ShouldBe("ACME001");
        acme.GetProperty("helpUri").GetString().ShouldBe("https://example.com/rules/ACME001");
        acme.GetProperty("defaultConfiguration").GetProperty("level").GetString().ShouldBe("error");
        rules.First().GetProperty("helpUri").GetString()!.ShouldEndWith("docs/rules.md");

        var acmeResult = run.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("ruleId").GetString() == "ACME001");
        acmeResult.GetProperty("ruleIndex").GetInt32().ShouldBe(rules.Count - 1);
    }

    [Test]
    public void Sarif_omits_the_help_uri_of_a_plugin_rule_without_one()
    {
        var rule = new RuleDescriptor("PAY001", "Undocumented", Severity.Info, "Description.");
        var rules = RuleSet.With([new DelegatePlugin("PAY", [rule])]);

        var sarif = new SarifReportRenderer().Render(new ComparisonResult([]), new ReportContext("b", "h") { Rules = rules });
        var pay = JsonDocument.Parse(sarif).RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver")
            .GetProperty("rules").EnumerateArray().Last();

        pay.TryGetProperty("helpUri", out _).ShouldBeFalse();
        pay.GetProperty("help").GetProperty("text").GetString().ShouldBe("Description.");
    }
}
