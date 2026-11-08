using GapWatch.Core.Findings;
using Shouldly;

namespace GapWatch.Tests.Rules;

public sealed class ConstraintRuleTests
{
    private static string String(string constraints = "") => $$"""{ "type": "string"{{constraints}} }""";

    private static string Number(string constraints = "") => $$"""{ "type": "number"{{constraints}} }""";

    private static string RequestDiff(string before, string after) =>
        TestSpec.Diff(TestSpec.Request(before), TestSpec.Request(after)).RuleIds();

    private static string ResponseDiff(string before, string after) =>
        TestSpec.Diff(TestSpec.Response(before), TestSpec.Response(after)).RuleIds();

    // --- GW032 ValueRangeNarrowed / GW033 ValueRangeWidened --------------------------------------------------------

    [Test]
    public void Lowering_maxLength_breaks_requests_GW032_and_is_safe_in_responses()
    {
        var before = String(""", "maxLength": 100""");
        var after = String(""", "maxLength": 50""");

        var result = TestSpec.Diff(TestSpec.Request(before), TestSpec.Request(after));
        result.RuleIds().ShouldBe("GW032");
        result.Findings[0].Message.ShouldBe("MaxLength changed from 100 to 50; values between them are no longer accepted.");
        result.Findings[0].Severity.ShouldBe(Severity.Breaking);
        ResponseDiff(before, after).ShouldBe(string.Empty);
    }

    [Test]
    public void Raising_maxLength_warns_in_responses_GW033_and_is_safe_in_requests()
    {
        var before = String(""", "maxLength": 10""");
        var after = String(""", "maxLength": 40""");

        var result = TestSpec.Diff(TestSpec.Response(before), TestSpec.Response(after));
        result.RuleIds().ShouldBe("GW033");
        result.Findings[0].Severity.ShouldBe(Severity.Warning);
        RequestDiff(before, after).ShouldBe(string.Empty);
    }

    [Test]
    public void Adding_a_bound_narrows_and_removing_one_widens()
    {
        var free = String();
        var bounded = String(""", "minLength": 3""");

        RequestDiff(free, bounded).ShouldBe("GW032");
        ResponseDiff(free, bounded).ShouldBe(string.Empty);
        RequestDiff(bounded, free).ShouldBe(string.Empty);

        var removed = TestSpec.Diff(TestSpec.Response(bounded), TestSpec.Response(free));
        removed.RuleIds().ShouldBe("GW033");
        removed.Findings[0].Message.ShouldBe("The minLength of 3 was removed; any value can now be returned.");
    }

    [Test]
    public void Item_counts_and_numeric_ranges_are_compared_the_same_way()
    {
        const string Items = """{ "type": "array", "items": { "type": "string" }, "maxItems": 10 }""";

        RequestDiff(Items, Items.Replace("10", "5", StringComparison.Ordinal)).ShouldBe("GW032");
        ResponseDiff(Items, Items.Replace("10", "20", StringComparison.Ordinal)).ShouldBe("GW033");
        RequestDiff(Number(""", "minimum": 0"""), Number(""", "minimum": 1""")).ShouldBe("GW032");
        ResponseDiff(Number(""", "maximum": 100"""), Number(""", "maximum": 1000""")).ShouldBe("GW033");
    }

    [Test]
    public void An_inclusive_bound_that_becomes_exclusive_is_tighter()
    {
        var inclusive = Number(""", "minimum": 0""");
        var exclusive = Number(""", "exclusiveMinimum": 0""");

        var result = TestSpec.Diff(TestSpec.Request(inclusive), TestSpec.Request(exclusive));
        result.RuleIds().ShouldBe("GW032");
        result.Findings[0].Message.ShouldBe("Minimum changed from 0 to 0 (exclusive); values between them are no longer accepted.");
        ResponseDiff(exclusive, inclusive).ShouldBe("GW033");
    }

    [Test]
    public void OpenAPI_30_and_31_exclusive_bounds_mean_the_same()
    {
        var openApi30 = Number(""", "minimum": 5, "exclusiveMinimum": true, "maximum": 9, "exclusiveMaximum": true""");
        var openApi31 = Number(""", "exclusiveMinimum": 5, "exclusiveMaximum": 9""");

        RequestDiff(openApi30, openApi31).ShouldBe(string.Empty);
        ResponseDiff(openApi30, openApi31).ShouldBe(string.Empty);
    }

    [Test]
    public void Unchanged_bounds_report_nothing_and_allOf_keeps_the_tighter_one()
    {
        var bounded = String(""", "maxLength": 50""");
        RequestDiff(bounded, bounded).ShouldBe(string.Empty);

        var composed = """{ "allOf": [ { "type": "string", "maxLength": 80 }, { "maxLength": 50 } ] }""";
        RequestDiff(bounded, composed).ShouldBe(string.Empty);
        ResponseDiff(bounded, composed).ShouldBe(string.Empty);
    }

    // --- GW034 PatternAdded / GW035 PatternChanged -----------------------------------------------------------------

    [Test]
    public void Adding_a_pattern_breaks_requests_GW034_and_is_safe_in_responses()
    {
        var free = String();
        var constrained = String(""", "pattern": "^[A-Z]{3}$" """);

        var result = TestSpec.Diff(TestSpec.Request(free), TestSpec.Request(constrained));
        result.RuleIds().ShouldBe("GW034");
        result.Findings[0].Message.ShouldContain("'^[A-Z]{3}$'");
        ResponseDiff(free, constrained).ShouldBe(string.Empty);
    }

    [Test]
    public void Changing_a_pattern_warns_in_both_directions_GW035()
    {
        var before = String(""", "pattern": "^\\d{5}$" """);
        var after = String(""", "pattern": "^\\d{5}(-\\d{4})?$" """);

        RequestDiff(before, after).ShouldBe("GW035");
        ResponseDiff(before, after).ShouldBe("GW035");
    }

    [Test]
    public void Removing_a_pattern_warns_in_responses_GW035_and_is_safe_in_requests()
    {
        var constrained = String(""", "pattern": "^\\d+$" """);

        ResponseDiff(constrained, String()).ShouldBe("GW035");
        RequestDiff(constrained, String()).ShouldBe(string.Empty);
    }

    // --- GW036 AdditionalPropertiesRestricted ----------------------------------------------------------------------

    [Test]
    public void Forbidding_additional_properties_warns_in_requests_GW036_and_is_safe_in_responses()
    {
        const string Open = """{ "type": "object", "properties": { "name": { "type": "string" } } }""";
        const string Closed = """{ "type": "object", "properties": { "name": { "type": "string" } }, "additionalProperties": false }""";

        var result = TestSpec.Diff(TestSpec.Request(Open), TestSpec.Request(Closed));
        result.RuleIds().ShouldBe("GW036");
        result.Findings[0].Severity.ShouldBe(Severity.Warning);
        ResponseDiff(Open, Closed).ShouldBe(string.Empty);
        RequestDiff(Closed, Open).ShouldBe(string.Empty);
        RequestDiff(Open, Open.Replace("} } }", "} }, \"additionalProperties\": true }", StringComparison.Ordinal)).ShouldBe(string.Empty);
    }

    // --- GW037 DiscriminatorChanged --------------------------------------------------------------------------------

    private const string Components = """
        { "schemas": {
            "Cat": { "type": "object", "properties": { "kind": { "type": "string" }, "meows": { "type": "boolean" } } },
            "Dog": { "type": "object", "properties": { "kind": { "type": "string" }, "barks": { "type": "boolean" } } } } }
        """;

    private static string Pet(string property, string mapping) => $$"""
        { "oneOf": [ { "$ref": "#/components/schemas/Cat" }, { "$ref": "#/components/schemas/Dog" } ],
          "discriminator": { "propertyName": "{{property}}", "mapping": { {{mapping}} } } }
        """;

    private const string Both = """ "cat": "#/components/schemas/Cat", "dog": "#/components/schemas/Dog" """;

    [Test]
    public void Renaming_the_discriminator_property_breaks_both_directions_GW037()
    {
        TestSpec.Diff(TestSpec.Request(Pet("kind", Both), components: Components), TestSpec.Request(Pet("type", Both), components: Components))
            .RuleIds().ShouldBe("GW037");
        TestSpec.Diff(TestSpec.Response(Pet("kind", Both), components: Components), TestSpec.Response(Pet("type", Both), components: Components))
            .RuleIds().ShouldBe("GW037");
    }

    [Test]
    public void Remapping_a_discriminator_value_breaks_both_directions_and_removing_one_only_requests()
    {
        var swapped = """ "cat": "#/components/schemas/Dog", "dog": "#/components/schemas/Dog" """;
        var catOnly = """ "cat": "Cat" """;

        var remapped = TestSpec.Diff(TestSpec.Response(Pet("kind", Both), components: Components), TestSpec.Response(Pet("kind", swapped), components: Components));
        remapped.RuleIds().ShouldBe("GW037");
        remapped.Findings[0].Message.ShouldBe("Discriminator value 'cat' now selects 'Dog' instead of 'Cat'.");

        TestSpec.Diff(TestSpec.Request(Pet("kind", Both), components: Components), TestSpec.Request(Pet("kind", catOnly), components: Components))
            .Findings.Count(f => f.Rule.Id == "GW037").ShouldBe(1);
        TestSpec.Diff(TestSpec.Response(Pet("kind", Both), components: Components), TestSpec.Response(Pet("kind", catOnly), components: Components))
            .Findings.Count(f => f.Rule.Id == "GW037").ShouldBe(0);
    }

    [Test]
    public void A_mapping_by_name_and_by_reference_is_the_same()
    {
        var byName = """ "cat": "Cat", "dog": "Dog" """;

        TestSpec.Diff(TestSpec.Request(Pet("kind", Both), components: Components), TestSpec.Request(Pet("kind", byName), components: Components))
            .Findings.ShouldBeEmpty();
    }
}
