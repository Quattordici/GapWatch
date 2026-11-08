using GapWatch.Core.Findings;
using GapWatch.Reporting;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>Changes inside shared component schemas are attributed to the component and grouped across operations.</summary>
public sealed class FindingGroupTests
{
    private const string Pet = """{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" }, "owner": { "$ref": "#/components/schemas/Owner" } } }""";
    private const string PetNameOptional = """{ "type": "object", "properties": { "name": { "type": "string" }, "owner": { "$ref": "#/components/schemas/Owner" } } }""";
    private const string Owner = """{ "type": "object", "required": ["id"], "properties": { "id": { "type": "integer" } } }""";
    private const string OwnerIdGone = """{ "type": "object", "properties": { "other": { "type": "integer" } } }""";

    private static string Components(string pet, string owner = Owner) =>
        $$"""{ "schemas": { "Pet": {{pet}}, "Owner": {{owner}} } }""";

    private const string PetRef = """{ "$ref": "#/components/schemas/Pet" }""";

    /// <summary>GET /pets (array of Pet), GET /pets/{id} (Pet) and POST /pets (Pet in, Pet out).</summary>
    private static string PetApi(string components) =>
        TestSpec.Document(
            $$"""
            {
              "/pets": {
                "get": { "responses": { "200": { "description": "d", "content": { "application/json": { "schema": { "type": "array", "items": {{PetRef}} } } } } } },
                "post": {
                  "requestBody": { "required": true, "content": { "application/json": { "schema": {{PetRef}} } } },
                  "responses": { "201": { "description": "d", "content": { "application/json": { "schema": {{PetRef}} } } } }
                }
              },
              "/pets/{id}": {
                "get": {
                  "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
                  "responses": { "200": { "description": "d", "content": { "application/json": { "schema": {{PetRef}} } } } }
                }
              }
            }
            """,
            components);

    [Test]
    public void Findings_inside_a_shared_component_carry_the_component_and_its_relative_path()
    {
        var result = TestSpec.Diff(PetApi(Components(Pet)), PetApi(Components(PetNameOptional)));

        var responseFindings = result.Findings.Where(f => f.Rule == RuleCatalog.PropertyBecameOptional).ToList();
        string.Join(",", responseFindings.Select(f => f.Operation)).ShouldBe("GET /pets,GET /pets/{id},POST /pets");
        responseFindings.All(f => f.Origin == new SchemaOrigin("Pet", "$.name", Direction.Response)).ShouldBeTrue();
        responseFindings[0].Location.ShouldBe("response 200 application/json: $[].name");
    }

    [Test]
    public void Findings_in_the_same_component_are_grouped_but_counts_stay_per_finding()
    {
        var result = TestSpec.Diff(PetApi(Components(Pet)), PetApi(Components(PetNameOptional)));

        var groups = FindingGroup.Group(result.Findings);

        groups.ShouldHaveSingleItem().Members.Count.ShouldBe(3);
        groups[0].IsShared.ShouldBeTrue();
        result.Count(Severity.Breaking).ShouldBe(3);
    }

    [Test]
    public void Request_and_response_uses_of_a_component_are_separate_changes()
    {
        // Removing a required property: breaking in responses (GW025), a warning in requests (GW026).
        var result = TestSpec.Diff(PetApi(Components(Pet, Owner)), PetApi(Components(Pet, OwnerIdGone)));

        var groups = FindingGroup.Group(result.Findings);

        string.Join(" | ", groups.Select(g => $"{g.First.Rule.Id} {g.First.Origin!.Display} x{g.Members.Count}"))
            .ShouldBe("GW025 Owner $.id (response) x3 | GW026 Owner $.id (request) x1");
    }

    [Test]
    public void Changes_next_to_a_nested_ref_belong_to_the_referencing_component()
    {
        const string NullableOwner = """{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" }, "owner": { "allOf": [ { "$ref": "#/components/schemas/Owner" } ], "nullable": true } } }""";

        var result = TestSpec.Diff(PetApi(Components(Pet)), PetApi(Components(NullableOwner)));

        var nullability = result.Findings.Where(f => f.Rule == RuleCatalog.NullabilityChanged).ToList();
        nullability.Count.ShouldBe(3);
        nullability.All(f => f.Origin == new SchemaOrigin("Pet", "$.owner", Direction.Response)).ShouldBeTrue();
    }

    [Test]
    public void Inline_schemas_have_no_origin_and_are_never_grouped()
    {
        var before = TestSpec.Response("""{ "type": "object", "required": ["a"], "properties": { "a": { "type": "string" } } }""");
        var after = TestSpec.Response("""{ "type": "object", "properties": { "a": { "type": "string" } } }""");

        var finding = TestSpec.Diff(before, after).Findings.ShouldHaveSingleItem();

        finding.Origin.ShouldBeNull();
    }

    [Test]
    public void A_ref_that_points_to_a_different_component_has_no_origin()
    {
        var before = TestSpec.Response(
            """{ "$ref": "#/components/schemas/A" }""",
            components: """{ "schemas": { "A": { "type": "string" } } }""");
        var after = TestSpec.Response(
            """{ "$ref": "#/components/schemas/B" }""",
            components: """{ "schemas": { "B": { "type": "integer" } } }""");

        TestSpec.Diff(before, after).Findings.ShouldHaveSingleItem().Origin.ShouldBeNull();
    }

    [Test]
    public void Group_keeps_report_order_and_separates_different_messages()
    {
        var origin = new SchemaOrigin("Pet", "$.status", Direction.Response);
        Finding Added(string operation, string value) =>
            new(RuleCatalog.EnumValueAdded, operation, "loc", $"New enum value(s) can be returned: '{value}'.") { Origin = origin };
        var lone = new Finding(RuleCatalog.OperationIdChanged, "GET /b", "operation", "changed");

        var groups = FindingGroup.Group([Added("GET /a", "x"), lone, Added("GET /c", "x"), Added("GET /d", "y")]);

        string.Join(" | ", groups.Select(g => string.Join(",", g.Members.Select(m => m.Operation))))
            .ShouldBe("GET /a,GET /c | GET /b | GET /d");
    }

    [Test]
    public void Console_and_markdown_reports_show_one_entry_per_shared_change()
    {
        var result = TestSpec.Diff(PetApi(Components(Pet)), PetApi(Components(PetNameOptional)));
        var context = new ReportContext("v1.json", "v2.json");

        var console = new ConsoleReportRenderer().Render(result, context);
        var markdown = new MarkdownReportRenderer().Render(result, context);

        console.ShouldContain("BREAKING (3)\n  [GW029] Pet $.name (response), affects 3 operations\n");
        console.ShouldContain("          - GET /pets/{id}  response 200 application/json: $.name\n");
        markdown.ShouldContain("| GW029 | `GET /pets`<br>`GET /pets/{id}`<br>`POST /pets` | schema `Pet` `$.name` (response, 3 operations)<br>line ");
    }

    [Test]
    public void Console_and_markdown_reports_show_one_entry_per_api_version()
    {
        const string Get = """{ "get": { "responses": { "204": { "description": "d" } } } }""";
        var before = TestSpec.Parse(TestSpec.Document($$"""{ "/v1/pets": {{Get}}, "/v1/owners": {{Get}}, "/v2/pets": {{Get}} }"""));
        var after = TestSpec.Parse(TestSpec.Document($$"""{ "/v2/pets": {{Get}} }"""));
        var result = GapWatch.Core.Comparison.ApiVersionAnalyzer.Apply(
            before, after, new GapWatch.Core.Comparison.ContractComparer().Compare(before, after), GapWatch.Core.Comparison.VersioningOptions.Default);
        var context = new ReportContext("v1.json", "v2.json");

        var console = new ConsoleReportRenderer().Render(result, context);
        var markdown = new MarkdownReportRenderer().Render(result, context);

        console.ShouldContain(
            "BREAKING (2)\n  [GW060] API version v1, affects 2 operations\n"
            + "          API version 'v1' was removed without being deprecated first (2 operations).\n"
            + "          - GET /v1/owners\n          - GET /v1/pets\n");
        markdown.ShouldContain("| GW060 | `GET /v1/owners`<br>`GET /v1/pets` | API version `v1` (2 operations) | ");
    }
}
