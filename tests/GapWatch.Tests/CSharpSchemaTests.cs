using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Roslyn;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>DTO schemas from C#: properties, naming, required, enums, annotations, polymorphism.</summary>
public sealed class CSharpSchemaTests
{
    private const string Usings = """
        using System.ComponentModel.DataAnnotations;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using Microsoft.AspNetCore.Mvc;

        """;

    /// <summary>A request (POST body) and a response (GET) that both use <c>Dto</c>.</summary>
    private const string Endpoints = """
        [ApiController]
        public sealed class DtoController : ControllerBase
        {
            [HttpPost("dto")] public IActionResult Create(Dto dto) => Ok();
            [HttpGet("dto")] public ActionResult<Dto> Get() => Ok();
        }

        """;

    private static ApiContract Read(string source) => CSharpContractReader.FromSources([("Api.cs", Usings + Endpoints + source)]);

    private static SchemaContract Response(string source) =>
        Read(source).Operations.Single(o => o.Method == "GET").Responses.Single().Content["application/json"];

    private static string Properties(SchemaContract schema) =>
        string.Join(", ", schema.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}{(p.Value.Required ? "!" : "")} {Describe(p.Value.Schema)}"));

    private static string Describe(SchemaContract schema)
    {
        var text = schema.IsRecursiveReference ? $"<{schema.ReferenceName}>"
            : schema.Items is { } items ? $"{Describe(items)}[]"
            : schema.ReferenceName ?? (schema.Types.Count == 0 ? "any" : string.Join("|", schema.Types) + (schema.Format is null ? "" : $"/{schema.Format}"));
        if (schema.EnumValues.Count > 0)
        {
            text += "(" + string.Join(",", schema.EnumValues) + ")";
        }

        return text + (schema.Nullable ? "?" : "");
    }

    private static string RequestDiff(string before, string after) => Diff(before, after, "POST");

    private static string ResponseDiff(string before, string after) => Diff(before, after, "GET");

    private static string Diff(string before, string after, string method) =>
        string.Join(",", new ContractComparer().Compare(Read(before), Read(after)).Findings
            .Where(f => f.Operation.StartsWith(method, StringComparison.Ordinal))
            .Select(f => f.Rule.Id)
            .Order(StringComparer.Ordinal));

    // --- Properties ----------------------------------------------------------------------------------------------

    [Test]
    public void Properties_follow_system_text_json_names_and_skip_ignored_ones()
    {
        var schema = Response("""
            public class Base { public DateTimeOffset CreatedAt { get; set; } }
            public sealed class Dto : Base
            {
                public long Id { get; set; }
                public string? URLValue { get; set; }
                [JsonPropertyName("display_name")] public string Name { get; set; } = "";
                [JsonIgnore] public string Secret { get; set; } = "";
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }
                public IReadOnlyList<string> Tags { get; } = [];
                public static int Count { get; set; }
                internal int Hidden { get; set; }
            }
            """);

        schema.ReferenceName.ShouldBe("Dto");
        Properties(schema).ShouldBe(
            "createdAt string/date-time, display_name string, id integer/int64, note string?, tags string[], urlValue string?");
    }

    [Test]
    public void Required_comes_from_the_modifier_the_attributes_and_the_only_public_constructor()
    {
        Properties(Response("""
            public sealed class Dto
            {
                public required string Name { get; init; }
                [Required] public string? Owner { get; set; }
                [JsonRequired] public int Age { get; set; }
                public string? Nickname { get; set; }
            }
            """)).ShouldBe("age! integer/int32, name! string, nickname string?, owner! string?");

        Properties(Response("public sealed record Dto(long Id, string? Name) { public int Extra { get; init; } }"))
            .ShouldBe("extra integer/int32, id! integer/int64, name! string?");
    }

    [Test]
    public void A_naming_policy_set_in_the_json_options_applies_to_every_property()
    {
        const string Dto = "public sealed class Dto { public long PetId { get; set; } public string URLValue { get; set; } = \"\"; }";

        Properties(Response(Dto + """
            public static class Setup { public static void Configure(JsonSerializerOptions o) => o.PropertyNamingPolicy = null; }
            """)).ShouldBe("PetId integer/int64, URLValue string");
        Properties(Response(Dto + """
            public static class Setup { public static void Configure(JsonSerializerOptions o) => o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; }
            """)).ShouldBe("pet_id integer/int64, url_value string");
    }

    [Test]
    public void Generic_types_are_named_like_the_generators_and_cycles_are_cut()
    {
        var schema = Response("""
            public sealed class Page<T> { public List<T> Items { get; set; } = []; }
            public sealed class Node { public Node? Next { get; set; } }
            public sealed class Dto { public Page<Node> Nodes { get; set; } = new(); }
            """);

        var nodes = schema.Properties["nodes"].Schema;
        nodes.ReferenceName.ShouldBe("PageOfNode");
        Properties(nodes.Properties["items"].Schema.Items!).ShouldBe("next <Node>?");
    }

    // --- Enums ---------------------------------------------------------------------------------------------------

    [Test]
    public void Enums_are_integers_unless_a_string_converter_applies()
    {
        const string Enums = """
            public enum Size { Small = 1, Large = 2 }
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public enum Color { Red, [JsonStringEnumMemberName("dark-blue")] DarkBlue }
            public sealed class Dto
            {
                public Size Size { get; set; }
                public Color Color { get; set; }
                [JsonConverter(typeof(JsonStringEnumConverter<Size>))] public Size? Named { get; set; }
            }
            """;

        Properties(Response(Enums)).ShouldBe("color string(Red,dark-blue), named string(Large,Small)?, size integer(1,2)");

        var global = Enums + """
            public static class Setup { public static void Configure(JsonSerializerOptions o) => o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)); }
            """;
        Properties(Response(global)).ShouldBe("color string(Red,dark-blue), named string(Large,Small)?, size string(large,small)");
    }

    // --- Annotations ---------------------------------------------------------------------------------------------

    [Test]
    public void Data_annotations_become_value_constraints()
    {
        var schema = Response("""
            public sealed class Dto
            {
                [Range(1, 100)] public int Count { get; set; }
                [Range(0.0, 1.0, MinimumIsExclusive = true)] public double Ratio { get; set; }
                [StringLength(50, MinimumLength = 3)] public string Name { get; set; } = "";
                [MaxLength(5)] public List<string> Tags { get; set; } = [];
                [Length(2, 8)] public string Code { get; set; } = "";
                [RegularExpression("^[A-Z]{3}$")] public string Currency { get; set; } = "";
            }
            """);

        static string Constraints(SchemaContract s) => string.Join(" ", new[]
        {
            s.Minimum is { } min ? $"min={min}" : null,
            s.Maximum is { } max ? $"max={max}" : null,
            s.MinLength is { } minLength ? $"minLength={minLength}" : null,
            s.MaxLength is { } maxLength ? $"maxLength={maxLength}" : null,
            s.MaxItems is { } maxItems ? $"maxItems={maxItems}" : null,
            s.Pattern is { } pattern ? $"pattern={pattern}" : null,
        }.OfType<string>());

        string.Join(" | ", schema.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}: {Constraints(p.Value.Schema)}"))
            .ShouldBe("code: minLength=2 maxLength=8 | count: min=1 max=100 | currency: pattern=^[A-Z]{3}$ | name: minLength=3 maxLength=50 | ratio: min=0 (exclusive) max=1 | tags: maxItems=5");
    }

    [Test]
    public void Annotations_on_parameters_constrain_the_parameter()
    {
        var operation = CSharpContractReader.FromSources([("Api.cs", Usings + """
            public sealed class C : ControllerBase
            {
                [HttpGet("pets")] public IActionResult List([Range(1, 50)] int limit = 10) => Ok();
            }
            """)]).Operations.Single();

        operation.Parameters.Single().Schema.Maximum.ShouldBe(new Bound(50));
    }

    // --- Polymorphism --------------------------------------------------------------------------------------------

    private const string Pets = """
        [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
        [JsonDerivedType(typeof(Cat), "cat")]
        [JsonDerivedType(typeof(Dog), "dog")]
        public abstract class Pet { public string Name { get; set; } = ""; }
        public sealed class Cat : Pet { public bool Meows { get; set; } }
        public sealed class Dog : Pet { public bool Barks { get; set; } }
        public sealed class Dto { public required Pet Pet { get; set; } }
        """;

    [Test]
    public void Derived_types_are_variants_with_a_discriminator()
    {
        var pet = Response(Pets).Properties["pet"].Schema;

        pet.Discriminator!.PropertyName.ShouldBe("kind");
        string.Join(", ", pet.Discriminator.Mapping.Select(m => $"{m.Key}={m.Value}")).ShouldBe("cat=Cat, dog=Dog");
        string.Join(" | ", pet.Variants.Select(v => $"{v.ReferenceName}: {Properties(v)}"))
            .ShouldBe("Cat: kind! string(cat), meows boolean, name string | Dog: barks boolean, kind! string(dog), name string");
    }

    // --- Comparing two versions of the code ----------------------------------------------------------------------

    [Test]
    public void Removing_a_property_warns_both_ways_and_adding_one_is_safe_both_ways()
    {
        const string Before = "public sealed class Dto { public long Id { get; set; } public string? Name { get; set; } }";
        const string After = "public sealed class Dto { public long Id { get; set; } }";

        // A request property the server stops reading is silently ignored or rejected, so it warns too (as for documents).
        ResponseDiff(Before, After).ShouldBe("GW026");
        RequestDiff(Before, After).ShouldBe("GW026");
        ResponseDiff(After, Before).ShouldBe(string.Empty);
        RequestDiff(After, Before).ShouldBe(string.Empty);
    }

    [Test]
    public void A_new_required_property_breaks_requests_and_is_safe_in_responses()
    {
        const string Before = "public sealed class Dto { public long Id { get; set; } }";
        const string After = "public sealed class Dto { public long Id { get; set; } public required string Owner { get; set; } }";

        RequestDiff(Before, After).ShouldBe("GW027");
        ResponseDiff(Before, After).ShouldBe(string.Empty);
    }

    [Test]
    public void Renaming_with_JsonPropertyName_is_a_removal_but_renaming_the_csharp_property_alone_is_not()
    {
        const string Before = "public sealed class Dto { public string? Name { get; set; } }";

        ResponseDiff(Before, "public sealed class Dto { [JsonPropertyName(\"title\")] public string? Name { get; set; } }").ShouldBe("GW026");
        ResponseDiff(Before, "public sealed class Dto { [JsonPropertyName(\"name\")] public string? Title { get; set; } }").ShouldBe(string.Empty);
    }

    [Test]
    public void Lowering_MaxLength_breaks_requests_and_is_safe_in_responses()
    {
        const string Before = "public sealed class Dto { [MaxLength(100)] public string? Name { get; set; } }";
        var after = Before.Replace("100", "50", StringComparison.Ordinal);

        RequestDiff(Before, after).ShouldBe("GW032");
        ResponseDiff(Before, after).ShouldBe(string.Empty);
    }

    [Test]
    public void Adding_an_enum_member_warns_in_responses_and_is_safe_in_requests()
    {
        const string Before = "public enum Size { Small, Large } public sealed class Dto { public Size Size { get; set; } }";
        var after = Before.Replace("Large }", "Large, Huge }", StringComparison.Ordinal);

        ResponseDiff(Before, after).ShouldBe("GW024");
        RequestDiff(Before, after).ShouldBe(string.Empty);
    }

    [Test]
    public void Switching_enums_to_strings_changes_the_type_in_both_directions()
    {
        const string Before = "public enum Size { Small, Large } public sealed class Dto { public Size Size { get; set; } }";
        var after = "[JsonConverter(typeof(JsonStringEnumConverter))] " + Before;

        RequestDiff(Before, after).ShouldContain("GW020");
        ResponseDiff(Before, after).ShouldContain("GW020");
    }

    [Test]
    public void Renaming_the_discriminator_breaks_both_directions_and_adding_a_subtype_only_warns_responses()
    {
        var renamed = Pets.Replace("\"kind\"", "\"type\"", StringComparison.Ordinal);
        RequestDiff(Pets, renamed).ShouldContain("GW037");
        ResponseDiff(Pets, renamed).ShouldContain("GW037");

        var withBird = Pets
            .Replace("""[JsonDerivedType(typeof(Dog), "dog")]""", """[JsonDerivedType(typeof(Dog), "dog")][JsonDerivedType(typeof(Bird), "bird")]""", StringComparison.Ordinal)
            + "public sealed class Bird : Pet { }";
        ResponseDiff(Pets, withBird).ShouldBe("GW031");
        RequestDiff(Pets, withBird).ShouldBe(string.Empty);
    }

    [Test]
    public void Findings_in_a_dto_point_at_the_property_in_its_own_file()
    {
        const string Controller = """
            using Microsoft.AspNetCore.Mvc;
            [ApiController]
            public sealed class DtoController : ControllerBase
            {
                [HttpPost("dto")] public IActionResult Create(Dto dto) => Ok();
            }
            """;
        static ApiContract Version(string dto) => CSharpContractReader.FromSources([("Controller.cs", Controller), ("Dto.cs", dto)]);

        var result = new ContractComparer().Compare(
            Version("public sealed class Dto\n{\n    public int Count { get; set; }\n}"),
            Version("public sealed class Dto\n{\n    [System.ComponentModel.DataAnnotations.Range(1, 9)]\n    public int Count { get; set; }\n}"));

        // [Range(1, 9)] adds a minimum and a maximum.
        result.RuleIds().ShouldBe("GW032,GW032");
        var finding = result.Findings[0];
        finding.Document.ShouldBe("Dto.cs");
        finding.Position!.Line.ShouldBe(4);
        finding.Origin!.Component.ShouldBe("Dto");
    }

    [Test]
    public void AsParameters_binds_each_property_as_a_parameter()
    {
        var operation = CSharpContractReader.FromSources([("Api.cs", """
            public sealed record Paging(int Page = 1, [property: System.ComponentModel.DataAnnotations.Range(1, 100)] int? Size = null);
            public sealed record Filter(string Status, Paging? Ignored);
            public static class Endpoints
            {
                public static void Map(WebApplication app) =>
                    app.MapGet("/pets", ([AsParameters] Filter filter, [AsParameters] Paging paging) => Results.Ok());
            }
            """)]).Operations.Single();

        string.Join(", ", operation.Parameters.Select(p => $"{p.Name}{(p.Required ? "!" : "")}")).ShouldBe("Status!, Page, Size");
        operation.Parameters.Single(p => p.Name == "Size").Schema.Maximum.ShouldBe(new Bound(100));
    }
}
