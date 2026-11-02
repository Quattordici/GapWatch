using Shouldly;

namespace GapWatch.Tests.Rules;

/// <summary>The same structural change is breaking or safe depending on direction; these tests pin that down.</summary>
public sealed class SchemaRuleTests
{
    private const string Str = """{ "type": "string" }""";

    private static string Obj(string properties, string required = "") =>
        $$"""{ "type": "object", "required": [ {{required}} ], "properties": { {{properties}} } }""";

    // --- Responses: consumers read the value ---------------------------------------------------------------------

    [Fact]
    public void Response_required_property_removed_is_breaking_GW025()
    {
        var before = TestSpec.Response(Obj("\"name\": " + Str, "\"name\""));
        var after = TestSpec.Response(Obj("\"other\": " + Str));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW025");
    }

    [Fact]
    public void Response_optional_property_removed_warns_GW026()
    {
        var before = TestSpec.Response(Obj("\"name\": " + Str));
        var after = TestSpec.Response(Obj("\"other\": " + Str));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW026");
    }

    [Fact]
    public void Response_property_added_is_safe()
    {
        var before = TestSpec.Response(Obj("\"name\": " + Str));
        var after = TestSpec.Response(Obj("\"name\": " + Str + ", \"extra\": " + Str));

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Response_type_change_is_breaking_GW020()
    {
        var before = TestSpec.Response(Obj("\"id\": { \"type\": \"integer\" }"));
        var after = TestSpec.Response(Obj("\"id\": " + Str));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW020");
    }

    [Fact]
    public void Response_integer_widened_to_number_is_breaking_GW020()
    {
        var before = TestSpec.Response("""{ "type": "integer" }""");
        var after = TestSpec.Response("""{ "type": "number" }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW020");
    }

    [Fact]
    public void Response_number_narrowed_to_integer_is_safe()
    {
        var before = TestSpec.Response("""{ "type": "number" }""");
        var after = TestSpec.Response("""{ "type": "integer" }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Response_required_property_becoming_optional_is_breaking_GW029()
    {
        var before = TestSpec.Response(Obj("\"name\": " + Str, "\"name\""));
        var after = TestSpec.Response(Obj("\"name\": " + Str));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW029");
    }

    [Fact]
    public void Response_value_becoming_nullable_is_breaking_GW022()
    {
        var before = TestSpec.Response("""{ "type": "string" }""");
        var after = TestSpec.Response("""{ "type": "string", "nullable": true }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW022");
    }

    [Fact]
    public void Response_value_no_longer_nullable_is_safe()
    {
        var before = TestSpec.Response("""{ "type": "string", "nullable": true }""");
        var after = TestSpec.Response("""{ "type": "string" }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Response_enum_value_added_warns_GW024()
    {
        var before = TestSpec.Response("""{ "type": "string", "enum": [ "a", "b" ] }""");
        var after = TestSpec.Response("""{ "type": "string", "enum": [ "a", "b", "c" ] }""");

        var finding = TestSpec.Diff(before, after).Findings.ShouldHaveSingleItem();

        finding.Rule.Id.ShouldBe("GW024");
        finding.Message.ShouldContain("'c'");
    }

    [Fact]
    public void Response_enum_value_removed_is_safe()
    {
        var before = TestSpec.Response("""{ "type": "string", "enum": [ "a", "b" ] }""");
        var after = TestSpec.Response("""{ "type": "string", "enum": [ "a" ] }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Response_enum_constraint_dropped_warns_GW024()
    {
        var before = TestSpec.Response("""{ "type": "string", "enum": [ "a" ] }""");
        var after = TestSpec.Response("""{ "type": "string" }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW024");
    }

    [Fact]
    public void Response_int32_widened_to_int64_is_breaking_GW021()
    {
        var before = TestSpec.Response("""{ "type": "integer", "format": "int32" }""");
        var after = TestSpec.Response("""{ "type": "integer", "format": "int64" }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW021");
    }

    [Fact]
    public void Response_int64_narrowed_to_int32_is_safe()
    {
        var before = TestSpec.Response("""{ "type": "integer", "format": "int64" }""");
        var after = TestSpec.Response("""{ "type": "integer", "format": "int32" }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Response_variant_added_warns_GW031()
    {
        var before = TestSpec.Response("""{ "oneOf": [ { "type": "string" }, { "type": "integer" } ] }""");
        var after = TestSpec.Response("""{ "oneOf": [ { "type": "string" }, { "type": "integer" }, { "type": "boolean" } ] }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW031");
    }

    [Fact]
    public void Response_variant_removed_is_safe()
    {
        var before = TestSpec.Response("""{ "oneOf": [ { "type": "string" }, { "type": "integer" }, { "type": "boolean" } ] }""");
        var after = TestSpec.Response("""{ "oneOf": [ { "type": "string" }, { "type": "integer" } ] }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Nested_array_item_changes_carry_a_precise_location()
    {
        var before = TestSpec.Response("{ \"type\": \"array\", \"items\": " + Obj("\"name\": " + Str, "\"name\"") + " }");
        var after = TestSpec.Response("{ \"type\": \"array\", \"items\": " + Obj("\"other\": " + Str) + " }");

        var finding = TestSpec.Diff(before, after).Findings.ShouldHaveSingleItem();

        finding.Rule.Id.ShouldBe("GW025");
        finding.Location.ShouldBe("response 200 application/json: $[].name");
    }

    [Fact]
    public void Removing_a_write_only_property_from_a_response_is_not_a_change()
    {
        var before = TestSpec.Response(Obj("\"password\": { \"type\": \"string\", \"writeOnly\": true }", "\"password\""));
        var after = TestSpec.Response(Obj("\"name\": " + Str));

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    // --- Requests: consumers send the value ----------------------------------------------------------------------

    [Fact]
    public void Request_new_required_property_is_breaking_GW027()
    {
        var before = TestSpec.Request(Obj("\"name\": " + Str, "\"name\""));
        var after = TestSpec.Request(Obj("\"name\": " + Str + ", \"owner\": " + Str, "\"name\", \"owner\""));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW027");
    }

    [Fact]
    public void Request_new_optional_property_is_safe()
    {
        var before = TestSpec.Request(Obj("\"name\": " + Str, "\"name\""));
        var after = TestSpec.Request(Obj("\"name\": " + Str + ", \"owner\": " + Str, "\"name\""));

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Request_property_becoming_required_is_breaking_GW028()
    {
        var before = TestSpec.Request(Obj("\"name\": " + Str));
        var after = TestSpec.Request(Obj("\"name\": " + Str, "\"name\""));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW028");
    }

    [Fact]
    public void Request_required_property_becoming_optional_is_safe()
    {
        var before = TestSpec.Request(Obj("\"name\": " + Str, "\"name\""));
        var after = TestSpec.Request(Obj("\"name\": " + Str));

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Request_property_removed_warns_GW026()
    {
        var before = TestSpec.Request(Obj("\"name\": " + Str + ", \"tag\": " + Str));
        var after = TestSpec.Request(Obj("\"name\": " + Str));

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW026");
    }

    [Fact]
    public void Request_enum_value_removed_is_breaking_GW023()
    {
        var before = TestSpec.Request("""{ "type": "string", "enum": [ "a", "b" ] }""");
        var after = TestSpec.Request("""{ "type": "string", "enum": [ "a" ] }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW023");
    }

    [Fact]
    public void Request_enum_value_added_is_safe()
    {
        var before = TestSpec.Request("""{ "type": "string", "enum": [ "a" ] }""");
        var after = TestSpec.Request("""{ "type": "string", "enum": [ "a", "b" ] }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Request_enum_constraint_introduced_is_breaking_GW023()
    {
        var before = TestSpec.Request("""{ "type": "string" }""");
        var after = TestSpec.Request("""{ "type": "string", "enum": [ "a" ] }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW023");
    }

    [Fact]
    public void Request_value_no_longer_accepting_null_is_breaking_GW022()
    {
        var before = TestSpec.Request("""{ "type": "string", "nullable": true }""");
        var after = TestSpec.Request("""{ "type": "string" }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW022");
    }

    [Fact]
    public void Request_value_becoming_nullable_is_safe()
    {
        var before = TestSpec.Request("""{ "type": "string" }""");
        var after = TestSpec.Request("""{ "type": "string", "nullable": true }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Request_integer_widened_to_number_is_safe()
    {
        var before = TestSpec.Request("""{ "type": "integer" }""");
        var after = TestSpec.Request("""{ "type": "number" }""");

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Request_number_narrowed_to_integer_is_breaking_GW020()
    {
        var before = TestSpec.Request("""{ "type": "number" }""");
        var after = TestSpec.Request("""{ "type": "integer" }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW020");
    }

    [Fact]
    public void Request_int32_widened_to_int64_is_safe_but_the_reverse_is_breaking()
    {
        var int32 = TestSpec.Request("""{ "type": "integer", "format": "int32" }""");
        var int64 = TestSpec.Request("""{ "type": "integer", "format": "int64" }""");

        TestSpec.Diff(int32, int64).Findings.ShouldBeEmpty();
        TestSpec.Diff(int64, int32).RuleIds().ShouldBe("GW021");
    }

    [Fact]
    public void Request_format_constraint_introduced_is_breaking_GW021()
    {
        var before = TestSpec.Request("""{ "type": "string" }""");
        var after = TestSpec.Request("""{ "type": "string", "format": "uuid" }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW021");
    }

    [Fact]
    public void Request_variant_removed_is_breaking_GW030_and_added_is_safe()
    {
        var two = TestSpec.Request("""{ "oneOf": [ { "type": "string" }, { "type": "integer" } ] }""");
        var three = TestSpec.Request("""{ "oneOf": [ { "type": "string" }, { "type": "integer" }, { "type": "boolean" } ] }""");

        TestSpec.Diff(three, two).RuleIds().ShouldBe("GW030");
        TestSpec.Diff(two, three).Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Adding_a_required_read_only_property_to_a_request_schema_is_not_a_change()
    {
        var before = TestSpec.Request(Obj("\"name\": " + Str, "\"name\""));
        var after = TestSpec.Request(
            Obj("\"name\": " + Str + ", \"id\": { \"type\": \"integer\", \"readOnly\": true }", "\"name\", \"id\""));

        TestSpec.Diff(before, after).Findings.ShouldBeEmpty();
    }

    // --- Structure -----------------------------------------------------------------------------------------------

    [Fact]
    public void Recursive_schemas_terminate_and_still_detect_changes()
    {
        const string Components = """
            { "schemas": { "Node": { "type": "object", "required": [ "value" ], "properties": {
                "value": { "type": "string" },
                "next": { "$ref": "#/components/schemas/Node" } } } } }
            """;
        const string Changed = """
            { "schemas": { "Node": { "type": "object", "required": [ "value" ], "properties": {
                "value": { "type": "integer" },
                "next": { "$ref": "#/components/schemas/Node" } } } } }
            """;

        var before = TestSpec.Response("""{ "$ref": "#/components/schemas/Node" }""", components: Components);
        var after = TestSpec.Response("""{ "$ref": "#/components/schemas/Node" }""", components: Changed);

        var finding = TestSpec.Diff(before, after).Findings.ShouldHaveSingleItem();

        finding.Rule.Id.ShouldBe("GW020");
        finding.Location.ShouldBe("response 200 application/json: $.value");
    }

    [Fact]
    public void Type_changes_stop_deeper_comparison_to_avoid_noise()
    {
        var before = TestSpec.Response(Obj("\"a\": " + Str, "\"a\""));
        var after = TestSpec.Response("""{ "type": "string" }""");

        TestSpec.Diff(before, after).RuleIds().ShouldBe("GW020");
    }
}
