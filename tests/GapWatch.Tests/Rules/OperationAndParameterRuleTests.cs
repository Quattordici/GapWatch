using Shouldly;

namespace GapWatch.Tests.Rules;

public sealed class OperationAndParameterRuleTests
{
    private const string Int = """{ "type": "integer" }""";
    private const string Str = """{ "type": "string" }""";

    // --- Operations -------------------------------------------------------------------------------------------

    [Test]
    public void Identical_contracts_have_no_findings()
    {
        var doc = TestSpec.Response(Str);

        TestSpec.Diff(doc, doc).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Removing_an_operation_is_breaking_GW001()
    {
        var before = TestSpec.Operation("get", "/pets");
        var after = TestSpec.Operation("get", "/animals");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW001,GW003");
    }

    [Test]
    public void Adding_an_operation_is_informational_GW003()
    {
        var before = TestSpec.Operation("get", "/pets");
        var after = TestSpec.Document(
            """{ "/pets": { "get": { "responses": { "204": { "description": "d" } } }, "post": { "responses": { "204": { "description": "d" } } } } }""");

        var result = TestSpec.Diff(before, after);

        result.RuleIds().ShouldBe("GW003");
        result.Findings.ShouldHaveSingleItem().Operation.ShouldBe("POST /pets");
    }

    [Test]
    public void Deprecating_an_operation_is_informational_GW002()
    {
        var before = TestSpec.Operation("get", "/pets", "\"responses\": { \"204\": { \"description\": \"d\" } }");
        var after = TestSpec.Operation("get", "/pets", "\"deprecated\": true, \"responses\": { \"204\": { \"description\": \"d\" } }");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW002");
    }

    [Test]
    public void Changing_the_operationId_warns_GW004()
    {
        var before = TestSpec.Operation("get", "/pets", "\"operationId\": \"listPets\", \"responses\": { \"204\": { \"description\": \"d\" } }");
        var after = TestSpec.Operation("get", "/pets", "\"operationId\": \"getPets\", \"responses\": { \"204\": { \"description\": \"d\" } }");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW004");
    }

    [Test]
    public void Renaming_a_path_parameter_is_not_a_change()
    {
        var before = TestSpec.Document(
            """{ "/pets/{petId}": { "get": { "parameters": [ { "name": "petId", "in": "path", "required": true, "schema": { "type": "integer" } } ], "responses": { "204": { "description": "d" } } } } }""");
        var after = TestSpec.Document(
            """{ "/pets/{id}": { "get": { "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } } ], "responses": { "204": { "description": "d" } } } } }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Changing_a_path_parameter_type_is_still_detected_after_a_rename()
    {
        var before = TestSpec.Document(
            """{ "/pets/{petId}": { "get": { "parameters": [ { "name": "petId", "in": "path", "required": true, "schema": { "type": "integer" } } ], "responses": { "204": { "description": "d" } } } } }""");
        var after = TestSpec.Document(
            """{ "/pets/{id}": { "get": { "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "204": { "description": "d" } } } } }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW020");
    }

    // --- Parameters -------------------------------------------------------------------------------------------

    [Test]
    public void Adding_a_required_parameter_is_breaking_GW010()
    {
        var before = TestSpec.Operation("get", "/x");
        var after = TestSpec.Parameter("region", "query", Str, required: true);

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW010");
    }

    [Test]
    public void Adding_an_optional_parameter_is_safe()
    {
        var before = TestSpec.Operation("get", "/x");
        var after = TestSpec.Parameter("region", "query", Str);

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Making_a_parameter_required_is_breaking_GW011()
    {
        var before = TestSpec.Parameter("region", "query", Str);
        var after = TestSpec.Parameter("region", "query", Str, required: true);

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW011");
    }

    [Test]
    public void Removing_a_parameter_warns_GW012()
    {
        var before = TestSpec.Parameter("region", "query", Str);
        var after = TestSpec.Operation("get", "/x");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW012");
    }

    [Test]
    public void Changing_a_parameter_type_is_breaking_GW020()
    {
        var before = TestSpec.Parameter("limit", "query", Str);
        var after = TestSpec.Parameter("limit", "query", Int);

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW020");
    }

    [Test]
    public void Widening_a_parameter_integer_to_number_is_safe()
    {
        var before = TestSpec.Parameter("limit", "query", Int);
        var after = TestSpec.Parameter("limit", "query", """{ "type": "number" }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Header_parameter_names_are_case_insensitive()
    {
        var before = TestSpec.Parameter("X-Request-Id", "header", Str);
        var after = TestSpec.Parameter("x-request-id", "header", Str);

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Parameter_findings_point_at_the_parameter()
    {
        var before = TestSpec.Parameter("limit", "query", Str);
        var after = TestSpec.Parameter("limit", "query", Int);

        TestSpec.Diff(before, after).Findings.ShouldHaveSingleItem().Location.ShouldBe("parameter 'limit' (query)");
    }
}
