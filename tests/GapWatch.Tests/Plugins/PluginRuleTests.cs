using GapWatch.Core;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Core.Policy;
using GapWatch.Sdk;
using Shouldly;

namespace GapWatch.Tests.Plugins;

public sealed class PluginRuleTests
{
    private const string NoContent = "\"responses\": { \"204\": { \"description\": \"d\" } }";

    private static ApiContract Pets(string operations) =>
        TestSpec.Parse(TestSpec.Document($$"""{ "/pets": { {{operations}} } }"""));

    private static readonly ApiContract OnlyGet = Pets($$""" "get": { {{NoContent}} } """);

    private static readonly ApiContract GetAndPost = Pets($$""" "get": { {{NoContent}} }, "post": { {{NoContent}} } """);

    private static GapWatchConfiguration With(CompatibilityPolicy policy) => GapWatchConfiguration.Default with { Policy = policy };

    private static CompatibilityPolicy Policy(
        Dictionary<string, Severity>? overrides = null, HashSet<string>? disabled = null, params Suppression[] suppressions) =>
        new(overrides ?? new Dictionary<string, Severity>(StringComparer.Ordinal), disabled ?? new HashSet<string>(StringComparer.Ordinal), suppressions);

    private static string Describe(IEnumerable<Finding> findings) =>
        string.Join(" | ", findings.Select(f => $"{f.Rule.Id} {f.Severity} {f.Operation} {f.Location}"));

    [Test]
    public void A_plugin_finding_is_reported_with_the_built_in_findings()
    {
        var result = CompatibilityCheck.Run(OnlyGet, GetAndPost, GapWatchConfiguration.Default, rules: Acme.Rules()).Result;

        Describe(result.Findings).ShouldBe("ACME001 Breaking POST /pets operation | GW003 Info POST /pets operation");
    }

    [Test]
    public void A_plugin_sees_matched_and_removed_operations_too_and_reports_only_what_it_decides()
    {
        // ACME001 is about added operations: the unchanged GET and the removed POST are not reported by it.
        var result = CompatibilityCheck.Run(GetAndPost, OnlyGet, GapWatchConfiguration.Default, rules: Acme.Rules()).Result;

        Describe(result.Findings).ShouldBe("GW001 Breaking POST /pets operation");
    }

    [Test]
    public void Every_operation_pair_reaches_the_plugin_once()
    {
        var seen = new List<string>();
        var plugin = new DelegatePlugin("SEEN", [new RuleDescriptor("SEEN1", "Seen", Severity.Info, "d")], (pair, _) =>
        {
            seen.Add($"{(pair.IsAdded ? "added" : pair.IsRemoved ? "removed" : "matched")} {pair.Operation}");
            return [];
        });
        var before = Pets($$""" "get": { {{NoContent}} }, "delete": { {{NoContent}} } """);

        CompatibilityCheck.Run(before, GetAndPost, GapWatchConfiguration.Default, rules: RuleSet.With([plugin]));

        string.Join(" | ", seen).ShouldBe("matched GET /pets | removed DELETE /pets | added POST /pets");
    }

    [Test]
    public void Severity_overrides_apply_to_plugin_rules()
    {
        var policy = Policy(new Dictionary<string, Severity>(StringComparer.Ordinal) { ["ACME001"] = Severity.Warning });

        var result = CompatibilityCheck.Run(OnlyGet, GetAndPost, With(policy), rules: Acme.Rules()).Result;

        result.Findings.First(f => f.Rule.Id == "ACME001").Severity.ShouldBe(Severity.Warning);
    }

    [Test]
    public void A_plugin_rule_can_be_turned_off()
    {
        var policy = Policy(disabled: new HashSet<string>(StringComparer.Ordinal) { "ACME001" });

        var result = CompatibilityCheck.Run(OnlyGet, GetAndPost, With(policy), rules: Acme.Rules()).Result;

        Describe(result.Findings).ShouldBe("GW003 Info POST /pets operation");
    }

    [Test]
    public void Suppressions_and_baselines_match_plugin_findings_by_their_location_string()
    {
        var policy = Policy(suppressions: new Suppression("ACME001", "POST /pets", "operation", "Public health check."));

        var check = CompatibilityCheck.Run(OnlyGet, GetAndPost, With(policy), rules: Acme.Rules());

        check.Result.Suppressed.ShouldHaveSingleItem().Finding.Rule.Id.ShouldBe("ACME001");

        var unsuppressed = CompatibilityCheck.Run(OnlyGet, GetAndPost, GapWatchConfiguration.Default, rules: Acme.Rules());
        var accepted = Baseline.From(unsuppressed.BeforeBaseline.Findings, "Known.");
        var again = CompatibilityCheck.Run(OnlyGet, GetAndPost, GapWatchConfiguration.Default, accepted, rules: Acme.Rules());
        again.Result.Findings.ShouldBeEmpty();
    }

    [Test]
    public void Configuration_for_a_plugin_rule_is_valid_only_when_the_plugin_is_loaded()
    {
        var policy = Policy(new Dictionary<string, Severity>(StringComparer.Ordinal) { ["ACME001"] = Severity.Info });

        policy.Validate().ShouldHaveSingleItem().ShouldContain("Unknown rule id 'ACME001'");
        policy.Validate(Acme.Rules()).ShouldBeEmpty();
        Should.Throw<ArgumentException>(() => CompatibilityCheck.Run(OnlyGet, GetAndPost, With(policy)));
    }

    [Test]
    public void An_operation_finding_takes_its_position_from_the_head_operation_never_from_the_plugin()
    {
        var finding = CompatibilityCheck.Run(OnlyGet, GetAndPost, GapWatchConfiguration.Default, rules: Acme.Rules())
            .Result.Findings.First(f => f.Rule.Id == "ACME001");

        finding.Position.ShouldNotBeNull();
        finding.Position.ShouldBe(GetAndPost.Operations.Single(o => o.Method == "POST").Position);
        finding.At.ShouldBe(FindingLocation.Operation.Instance);
    }

    [Test]
    public void A_finding_inside_the_operation_points_at_the_element_its_location_names()
    {
        // Where a built-in finding at the same location would point, so code scanning annotates the same line.
        var head = TestSpec.Parse(TestSpec.Operation("get", "/pets", """
            "parameters": [ { "name": "limit", "in": "query", "schema": { "type": "integer" } } ],
            "responses": { "200": { "description": "d", "content": { "application/json": { "schema": {
                "type": "object", "properties": { "items": { "type": "array", "items": { "type": "object",
                    "properties": { "name": { "type": "string" } } } } } } } } } }
            """));
        var rule = new RuleDescriptor("POS1", "Where", Severity.Info, "Reports at fixed locations.");
        FindingLocation[] locations =
        [
            new FindingLocation.Parameter("limit", "query"),
            new FindingLocation.ResponseContent("200", "application/json", "$.items[].name"),
            new FindingLocation.ResponseContent("200", "application/json", "$.missing"),
            new FindingLocation.Response("200"),
        ];
        var plugin = new DelegatePlugin("POS", [rule], (_, _) => locations.Select(at => new PluginFinding("POS1", at, "Here.")));

        var findings = CompatibilityCheck.Run(head, head, GapWatchConfiguration.Default, rules: RuleSet.With([plugin])).Result.Findings;

        var operation = head.Operations.Single();
        var response = operation.Responses.Single();
        var items = response.Content["application/json"].Properties["items"];
        Position(findings, "parameter 'limit' (query)").ShouldBe(operation.Parameters.Single().Position);
        Position(findings, "response 200 application/json: $.items[].name").ShouldBe(items.Schema.Items!.Properties["name"].Position);
        Position(findings, "response 200 application/json: $.missing").ShouldBe(response.Content["application/json"].Position);
        Position(findings, "response 200").ShouldBe(response.Position);
        operation.Parameters.Single().Position.ShouldNotBe(operation.Position);
    }

    private static SourcePosition? Position(IEnumerable<Finding> findings, string location) =>
        findings.Single(f => f.Location == location).Position;

    [Test]
    public void A_plugin_that_throws_stops_the_check_and_names_the_plugin_and_operation()
    {
        var plugin = new DelegatePlugin("BOOM", [new RuleDescriptor("BOOM1", "Boom", Severity.Info, "d")], (_, _) => throw new InvalidOperationException("bad state"));

        var error = Should.Throw<PluginException>(() =>
            CompatibilityCheck.Run(OnlyGet, OnlyGet, GapWatchConfiguration.Default, rules: RuleSet.With([plugin])));

        error.Message.ShouldBe("Plugin 'GapWatch.Tests' failed on operation 'GET /pets': InvalidOperationException: bad state");
    }

    [Test]
    public void A_plugin_that_throws_while_its_findings_are_enumerated_stops_the_check_too()
    {
        static IEnumerable<PluginFinding> Lazy()
        {
            yield return new PluginFinding("LAZY1", FindingLocation.Operation.Instance, "m");
            throw new InvalidOperationException("late");
        }

        var plugin = new DelegatePlugin("LAZY", [new RuleDescriptor("LAZY1", "Lazy", Severity.Info, "d")], (_, _) => Lazy());

        Should.Throw<PluginException>(() =>
            CompatibilityCheck.Run(OnlyGet, OnlyGet, GapWatchConfiguration.Default, rules: RuleSet.With([plugin])));
    }

    [Test]
    public void A_finding_for_an_undeclared_rule_stops_the_check()
    {
        var plugin = new DelegatePlugin("ACME", [Acme.NewOperationWithoutSecurity], (_, _) =>
            [new PluginFinding("ACME002", FindingLocation.Operation.Instance, "m")]);

        var error = Should.Throw<PluginException>(() =>
            CompatibilityCheck.Run(OnlyGet, OnlyGet, GapWatchConfiguration.Default, rules: RuleSet.With([plugin])));

        error.Message.ShouldContain("reported rule 'ACME002' on operation 'GET /pets', which it does not declare");
    }

    [Test]
    public void A_plugin_cannot_report_under_a_built_in_rule_id()
    {
        var plugin = new DelegatePlugin("ACME", [Acme.NewOperationWithoutSecurity], (_, _) =>
            [new PluginFinding("GW001", FindingLocation.Operation.Instance, "m")]);

        Should.Throw<PluginException>(() =>
            CompatibilityCheck.Run(OnlyGet, OnlyGet, GapWatchConfiguration.Default, rules: RuleSet.With([plugin])));
    }

    [Test]
    public void Without_plugins_the_result_is_the_same_as_before()
    {
        var withNone = CompatibilityCheck.Run(GetAndPost, OnlyGet, GapWatchConfiguration.Default, rules: RuleSet.With([])).Result;
        var builtIn = CompatibilityCheck.Run(GetAndPost, OnlyGet, GapWatchConfiguration.Default).Result;

        Describe(withNone.Findings).ShouldBe(Describe(builtIn.Findings));
    }
}
