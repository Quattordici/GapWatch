using Shouldly;

namespace GapWatch.Tests.Rules;

public sealed class BodyResponseAndSecurityRuleTests
{
    private const string Str = """{ "type": "string" }""";
    private const string NoContent = "\"responses\": { \"204\": { \"description\": \"d\" } }";

    private static string Post(string requestBody) =>
        TestSpec.Operation("post", "/x", $"\"requestBody\": {requestBody}, {NoContent}");

    private static string Json(string requestBody, string schema = Str) =>
        $$"""{ {{requestBody}} "content": { "application/json": { "schema": {{schema}} } } }""";

    // --- Request bodies ------------------------------------------------------------------------------------------

    [Test]
    public void Adding_a_required_request_body_is_breaking_GW040()
    {
        var before = TestSpec.Operation("post", "/x");
        var after = Post(Json("\"required\": true,"));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW040");
    }

    [Test]
    public void Adding_an_optional_request_body_is_safe()
    {
        var before = TestSpec.Operation("post", "/x");
        var after = Post(Json(string.Empty));

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Making_the_request_body_required_is_breaking_GW041()
    {
        var before = Post(Json(string.Empty));
        var after = Post(Json("\"required\": true,"));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW041");
    }

    [Test]
    public void Removing_the_request_body_warns_GW042()
    {
        var before = Post(Json("\"required\": true,"));
        var after = TestSpec.Operation("post", "/x");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW042");
    }

    [Test]
    public void Dropping_an_accepted_request_media_type_is_breaking_GW043()
    {
        var before = Post("""{ "content": { "application/json": { "schema": { "type": "string" } }, "application/xml": { "schema": { "type": "string" } } } }""");
        var after = Post("""{ "content": { "application/json": { "schema": { "type": "string" } } } }""");

        var finding = TestSpec.Diff(before, after).Findings.ShouldHaveSingleItem();

        finding.Rule.Id.ShouldBe("GW043");
        finding.Message.ShouldContain("application/xml");
    }

    [Test]
    public void Adding_an_accepted_request_media_type_is_safe()
    {
        var before = Post("""{ "content": { "application/json": { "schema": { "type": "string" } } } }""");
        var after = Post("""{ "content": { "application/json": { "schema": { "type": "string" } }, "application/xml": { "schema": { "type": "string" } } } }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Media_type_parameters_and_case_are_ignored()
    {
        var before = Post("""{ "content": { "application/json; charset=utf-8": { "schema": { "type": "string" } } } }""");
        var after = Post("""{ "content": { "Application/JSON": { "schema": { "type": "string" } } } }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    // --- Responses -----------------------------------------------------------------------------------------------

    [Test]
    public void Removing_a_success_response_is_breaking_GW044()
    {
        var before = TestSpec.Operation("get", "/x", "\"responses\": { \"200\": { \"description\": \"d\" } }");
        var after = TestSpec.Operation("get", "/x", "\"responses\": { \"201\": { \"description\": \"d\" } }");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW044");
    }

    [Test]
    public void Removing_an_error_response_is_informational_GW045()
    {
        var before = TestSpec.Operation("get", "/x", "\"responses\": { \"200\": { \"description\": \"d\" }, \"404\": { \"description\": \"d\" } }");
        var after = TestSpec.Operation("get", "/x", "\"responses\": { \"200\": { \"description\": \"d\" } }");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW045");
    }

    [Test]
    public void Dropping_a_response_media_type_is_breaking_GW043()
    {
        var before = TestSpec.Operation("get", "/x", """
            "responses": { "200": { "description": "d", "content": {
                "application/json": { "schema": { "type": "string" } },
                "text/csv": { "schema": { "type": "string" } } } } }
            """);
        var after = TestSpec.Operation("get", "/x", """
            "responses": { "200": { "description": "d", "content": {
                "application/json": { "schema": { "type": "string" } } } } }
            """);

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW043");
    }

    [Test]
    public void Status_codes_are_case_insensitive()
    {
        var before = TestSpec.Operation("get", "/x", "\"responses\": { \"4XX\": { \"description\": \"d\" } }");
        var after = TestSpec.Operation("get", "/x", "\"responses\": { \"4xx\": { \"description\": \"d\" } }");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Responses_are_resolved_through_component_references()
    {
        const string Components = """{ "responses": { "Ok": { "description": "d", "content": { "application/json": { "schema": { "type": "string" } } } } } }""";
        const string Changed = """{ "responses": { "Ok": { "description": "d", "content": { "application/json": { "schema": { "type": "integer" } } } } } }""";
        const string Paths = """{ "/x": { "get": { "responses": { "200": { "$ref": "#/components/responses/Ok" } } } } }""";

        TestSpec.Diff(TestSpec.Document(Paths, Components), TestSpec.Document(Paths, Changed))
            .RuleIds().ShouldBe("GW020");
    }

    // --- Security ------------------------------------------------------------------------------------------------

    [Test]
    public void Requiring_authentication_on_a_public_operation_is_breaking_GW050()
    {
        var before = TestSpec.Operation("get", "/x");
        var after = TestSpec.Operation(
            "get", "/x", NoContent + ", \"security\": [ { \"bearer\": [] } ]");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW050");
    }

    [Test]
    public void Making_a_secured_operation_public_is_safe()
    {
        var before = TestSpec.Operation("get", "/x", NoContent + ", \"security\": [ { \"bearer\": [] } ]");
        var after = TestSpec.Operation("get", "/x", NoContent + ", \"security\": []");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Dropping_one_of_several_security_alternatives_warns_GW051()
    {
        var before = TestSpec.Operation("get", "/x", NoContent + ", \"security\": [ { \"bearer\": [] }, { \"apiKey\": [] } ]");
        var after = TestSpec.Operation("get", "/x", NoContent + ", \"security\": [ { \"bearer\": [] } ]");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW051");
    }

    [Test]
    public void Replacing_every_security_alternative_is_breaking_GW052()
    {
        var before = TestSpec.Operation("get", "/x", NoContent + ", \"security\": [ { \"apiKey\": [] } ]");
        var after = TestSpec.Operation("get", "/x", NoContent + ", \"security\": [ { \"bearer\": [] } ]");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW052");
    }

    [Test]
    public void Adding_an_oauth_scope_counts_as_replacing_the_requirement()
    {
        var before = TestSpec.Operation("get", "/x", NoContent + ", \"security\": [ { \"oauth\": [ \"read\" ] } ]");
        var after = TestSpec.Operation("get", "/x", NoContent + ", \"security\": [ { \"oauth\": [ \"read\", \"write\" ] } ]");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW052");
    }

    [Test]
    public void Global_security_is_inherited_and_an_empty_operation_list_overrides_it()
    {
        var global = "\"security\": [ { \"bearer\": [] } ],";
        var inherited = TestSpec.Operation("get", "/x", NoContent, topLevel: global);
        var overridden = TestSpec.Operation("get", "/x", NoContent + ", \"security\": []", topLevel: global);

        // Inherited security means authentication is required; overriding with [] makes it public.
        TestSpec.Diff(inherited, overridden).Findings.ShouldBeEmpty();
        TestSpec.Diff(overridden, inherited).RuleIds().ShouldBe("GW050");
    }
}
