using Acme.GapWatch.Rules;
using GapWatch.Cli;
using GapWatch.Core;
using GapWatch.Core.Findings;
using GapWatch.Core.Policy;
using Shouldly;

namespace GapWatch.Tests.Plugins;

/// <summary>The sample plugin in <c>samples/</c>: both rules, both directions, and loaded from disk by the CLI.</summary>
public sealed class SamplePluginTests
{
    private static string Acme(string before, string after) =>
        string.Join(" | ", CompatibilityCheck.Run(TestSpec.Parse(before), TestSpec.Parse(after), GapWatchConfiguration.Default, rules: RuleSet.With([new AcmeRules()]))
            .Result.Findings.Where(f => f.Rule.Id.StartsWith("ACME", StringComparison.Ordinal))
            .Select(f => $"{f.Rule.Id} {f.Operation} {f.Location}"));

    private static string Object(string properties) => $$"""{ "type": "object", "properties": { {{properties}} } }""";

    [Test]
    public void ACME001_reports_a_new_open_operation()
    {
        Acme(TestSpec.Document("{}"), TestSpec.Document("""{ "/x": { "get": { "responses": { "204": { "description": "d" } } } } }"""))
            .ShouldBe("ACME001 GET /x operation");
    }

    [Test]
    public void ACME001_ignores_a_new_secured_operation_and_existing_open_ones()
    {
        const string schemes = """{ "securitySchemes": { "key": { "type": "apiKey", "in": "header", "name": "X-Key" } } }""";
        var secured = TestSpec.Document("""{ "/x": { "get": { "security": [ { "key": [] } ], "responses": { "204": { "description": "d" } } } } }""", schemes);
        var open = TestSpec.Document("""{ "/x": { "get": { "responses": { "204": { "description": "d" } } } } }""");

        Acme(TestSpec.Document("{}", schemes), secured).ShouldBeEmpty();
        Acme(open, open).ShouldBeEmpty();
    }

    [Test]
    public void ACME002_reports_a_new_unbounded_string_in_a_request()
    {
        Acme(
            TestSpec.Request(Object(""" "id": { "type": "integer" } """)),
            TestSpec.Request(Object(""" "id": { "type": "integer" }, "note": { "type": "string" } """)))
            .ShouldBe("ACME002 POST /x request body (application/json): $.note");
    }

    [Test]
    public void ACME002_ignores_the_same_string_in_a_response()
    {
        Acme(
            TestSpec.Response(Object(""" "id": { "type": "integer" } """)),
            TestSpec.Response(Object(""" "id": { "type": "integer" }, "note": { "type": "string" } """)))
            .ShouldBeEmpty();
    }

    [Test]
    public void ACME002_ignores_bounded_formatted_enum_and_existing_strings()
    {
        Acme(
            TestSpec.Request(Object(""" "old": { "type": "string" } """)),
            TestSpec.Request(Object(""" "old": { "type": "string" }, "code": { "type": "string", "maxLength": 8 }, "at": { "type": "string", "format": "date-time" }, "kind": { "type": "string", "enum": ["a"] } """)))
            .ShouldBeEmpty();
    }

    [Test]
    public void The_built_sample_loads_from_disk()
    {
        var path = typeof(AcmeRules).Assembly.Location;

        var plugins = PluginLoader.LoadAssembly(path);

        plugins.ShouldHaveSingleItem().Prefix.ShouldBe("ACME");
        plugins[0].GetType().ShouldNotBe(typeof(AcmeRules)); // its own load context, not the test's copy
    }
}
