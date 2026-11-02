using System.Text;
using GapWatch.Core;
using GapWatch.OpenApi;
using Shouldly;

namespace GapWatch.Tests;

public sealed class ReaderTests
{
    [Fact]
    public void Swagger_2_documents_are_rejected_with_a_helpful_message()
    {
        var ex = Should.Throw<ContractReadException>(() => TestSpec.Parse("""{ "swagger": "2.0", "paths": {} }"""));

        ex.Message.ShouldContain("Swagger 2.0");
    }

    [Fact]
    public void Documents_without_an_openapi_version_are_rejected()
    {
        Should.Throw<ContractReadException>(() => TestSpec.Parse("""{ "paths": {} }"""));
    }

    [Fact]
    public void Invalid_json_is_reported_as_a_read_error()
    {
        Should.Throw<ContractReadException>(() => TestSpec.Parse("{ not json"));
    }

    [Fact]
    public void External_references_are_rejected_with_a_helpful_message()
    {
        var doc = TestSpec.Response("""{ "$ref": "other.json#/components/schemas/Pet" }""");

        var ex = Should.Throw<ContractReadException>(() => TestSpec.Parse(doc));

        ex.Message.ShouldContain("local references");
    }

    [Fact]
    public void Unresolvable_references_are_reported()
    {
        var doc = TestSpec.Response("""{ "$ref": "#/components/schemas/Missing" }""");

        Should.Throw<ContractReadException>(() => TestSpec.Parse(doc));
    }

    [Fact]
    public void Title_version_and_operations_are_read()
    {
        var contract = TestSpec.Parse(TestSpec.Operation("get", "/pets", "\"operationId\": \"listPets\", \"responses\": { \"204\": { \"description\": \"d\" } }"));

        contract.Title.ShouldBe("t");
        contract.Version.ShouldBe("1");
        var operation = contract.Operations.ShouldHaveSingleItem();
        operation.Method.ShouldBe("GET");
        operation.OperationId.ShouldBe("listPets");
    }

    [Fact]
    public void AllOf_merges_properties_and_applies_required_from_the_composing_schema()
    {
        const string Components = """
            { "schemas": {
                "Base": { "type": "object", "properties": { "id": { "type": "integer" } } },
                "Pet": { "allOf": [ { "$ref": "#/components/schemas/Base" },
                                    { "type": "object", "properties": { "name": { "type": "string" } } } ],
                         "required": [ "id", "name" ] } } }
            """;

        var contract = TestSpec.Parse(TestSpec.Response("""{ "$ref": "#/components/schemas/Pet" }""", components: Components));

        var schema = contract.Operations.Single().Responses.Single().Content["application/json"];
        schema.ReferenceName.ShouldBe("Pet");
        string.Join(",", schema.Properties.Keys.OrderBy(k => k)).ShouldBe("id,name");
        schema.Properties["id"].Required.ShouldBeTrue();
        schema.Properties["name"].Required.ShouldBeTrue();
    }

    [Fact]
    public void Type_arrays_with_null_mean_nullable_in_openapi_3_1()
    {
        var contract = TestSpec.Parse(TestSpec.Response("""{ "type": [ "string", "null" ] }"""));

        var schema = contract.Operations.Single().Responses.Single().Content["application/json"];
        string.Join(",", schema.Types).ShouldBe("string");
        schema.Nullable.ShouldBeTrue();
    }

    [Fact]
    public void AnyOf_with_a_null_alternative_collapses_to_a_nullable_schema()
    {
        var contract = TestSpec.Parse(TestSpec.Response("""{ "anyOf": [ { "type": "string" }, { "type": "null" } ] }"""));

        var schema = contract.Operations.Single().Responses.Single().Content["application/json"];
        string.Join(",", schema.Types).ShouldBe("string");
        schema.Nullable.ShouldBeTrue();
        schema.Variants.ShouldBeEmpty();
    }

    [Fact]
    public void Missing_type_is_inferred_from_properties_and_items()
    {
        var obj = TestSpec.Parse(TestSpec.Response("""{ "properties": { "a": { "type": "string" } } }"""));
        var arr = TestSpec.Parse(TestSpec.Response("""{ "items": { "type": "string" } }"""));

        string.Join(",", obj.Operations.Single().Responses.Single().Content["application/json"].Types).ShouldBe("object");
        string.Join(",", arr.Operations.Single().Responses.Single().Content["application/json"].Types).ShouldBe("array");
    }

    [Fact]
    public void Path_level_parameters_are_merged_and_overridden_by_operation_level_ones()
    {
        var doc = TestSpec.Document("""
            { "/x": {
                "parameters": [ { "name": "q", "in": "query", "schema": { "type": "string" } },
                                { "name": "page", "in": "query", "schema": { "type": "integer" } } ],
                "get": { "parameters": [ { "name": "q", "in": "query", "required": true, "schema": { "type": "string" } } ],
                         "responses": { "204": { "description": "d" } } } } }
            """);

        var parameters = TestSpec.Parse(doc).Operations.Single().Parameters;

        parameters.Count.ShouldBe(2);
        parameters.Single(p => p.Name == "q").Required.ShouldBeTrue();
        parameters.Single(p => p.Name == "page").Required.ShouldBeFalse();
    }

    [Fact]
    public void Path_parameters_are_always_required()
    {
        var doc = TestSpec.Parameter("id", "path", """{ "type": "integer" }""");

        TestSpec.Parse(doc).Operations.Single().Parameters.Single().Required.ShouldBeTrue();
    }

    [Fact]
    public void Specification_extensions_next_to_paths_are_ignored()
    {
        var doc = TestSpec.Document("""{ "x-internal": { "ignored": true }, "/x": { "get": { "responses": { "204": { "description": "d" } } } } }""");

        TestSpec.Parse(doc).Operations.Count.ShouldBe(1);
    }

    [Fact]
    public void Shared_component_schemas_are_expanded_once_and_reused_without_changing_results()
    {
        // The same component is referenced many times; the cache must not leak state between uses.
        const string Components = """{ "schemas": { "Leaf": { "type": "object", "required": [ "v" ], "properties": { "v": { "type": "string" } } } } }""";
        const string Schema = """
            { "type": "object", "properties": {
                "a": { "$ref": "#/components/schemas/Leaf" },
                "b": { "$ref": "#/components/schemas/Leaf" },
                "c": { "type": "array", "items": { "$ref": "#/components/schemas/Leaf" } } } }
            """;

        var contract = TestSpec.Parse(TestSpec.Response(Schema, components: Components));

        var schema = contract.Operations.Single().Responses.Single().Content["application/json"];
        schema.Properties["a"].Schema.Properties["v"].Required.ShouldBeTrue();
        schema.Properties["b"].Schema.Properties["v"].Required.ShouldBeTrue();
        schema.Properties["c"].Schema.Items!.Properties["v"].Required.ShouldBeTrue();
    }

    [Fact]
    public void The_loader_explains_that_yaml_is_not_supported_yet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gapwatch-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, "openapi: 3.0.3", Encoding.UTF8);
        try
        {
            var ex = Should.Throw<ContractReadException>(() => ContractLoader.Load(path));

            ex.Message.ShouldContain("YAML");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_loader_reports_missing_files()
    {
        var ex = Should.Throw<ContractReadException>(() => ContractLoader.Load("/definitely/not/here.json"));

        ex.Message.ShouldContain("not found");
    }
}
