using System.Text;
using System.Text.Json;
using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.OpenApi;
using GapWatch.Reporting;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>Readers record where elements are declared, and findings carry the head position of what changed.</summary>
public sealed class SourcePositionTests
{
    // Line numbers below refer to this layout; keep it stable.
    private const string Head = """
        {
          "openapi": "3.0.3",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/pets": {
              "get": {
                "parameters": [
                  { "name": "q", "in": "query", "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": {
                    "description": "d",
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "Pet": {
                "type": "object",
                "required": [ "id" ],
                "properties": {
                  "id": { "type": "string" },
                  "owner": { "$ref": "#/components/schemas/Owner" }
                }
              },
              "Owner": { "type": "object", "properties": { "name": { "type": "string" } } }
            }
          }
        }
        """;

    private static ApiContract Json(string json) => TestSpec.Parse(json);

    private static ApiContract Yaml(string yaml) =>
        new OpenApiYamlReader().Read(new MemoryStream(Encoding.UTF8.GetBytes(yaml)));

    [Test]
    public void The_json_reader_records_the_position_of_each_element_key()
    {
        var operation = Json(Head).Operations.ShouldHaveSingleItem();

        operation.Position.ShouldBe(new SourcePosition(6, 7));
        operation.Parameters[0].Position.ShouldBe(new SourcePosition(8, 11));
        operation.Responses[0].Position.ShouldBe(new SourcePosition(11, 11));
    }

    [Test]
    public void A_referenced_schema_has_the_position_of_its_definition_and_a_property_the_position_of_its_name()
    {
        var pet = Json(Head).Operations[0].Responses[0].Content["application/json"];

        pet.Position.ShouldBe(new SourcePosition(21, 7));
        pet.Properties["id"].Position.ShouldBe(new SourcePosition(25, 11));
        pet.Properties["owner"].Position.ShouldBe(new SourcePosition(26, 11));
        pet.Properties["owner"].Schema.Position.ShouldBe(new SourcePosition(29, 7));
    }

    [Test]
    public void Columns_count_utf16_code_units_and_a_byte_order_mark_is_ignored()
    {
        var json = "﻿" + TestSpec.Document("""{ "/ö€": { "get": { "responses": {} } } }""");

        // "/ö€" is 3 characters but 6 bytes in UTF-8; the operation key starts right after it.
        var operation = Json(json).Operations.ShouldHaveSingleItem();
        var line = json.TrimStart('﻿');
        operation.Position.ShouldBe(new SourcePosition(1, line.IndexOf("\"get\"", StringComparison.Ordinal) + 1));
    }

    [Test]
    public void The_yaml_reader_records_positions_in_the_yaml_text()
    {
        var operation = Yaml("""
            openapi: 3.0.3
            info: { title: t, version: "1" }
            paths:
              /pets:
                get:
                  parameters:
                    - name: q
                      in: query
                  responses:
                    '200':
                      description: d
            """).Operations.ShouldHaveSingleItem();

        operation.Position.ShouldBe(new SourcePosition(5, 5));
        operation.Parameters[0].Position.ShouldBe(new SourcePosition(7, 11)); // a block sequence item starts after "- "
        operation.Responses[0].Position.ShouldBe(new SourcePosition(10, 9));
    }

    [Test]
    public void A_changed_property_points_at_the_property_and_a_removed_one_at_the_schema_it_was_removed_from()
    {
        var baseline = Json(Head.Replace("\"id\": { \"type\": \"string\" },", "\"id\": { \"type\": \"integer\" }, \"tag\": { \"type\": \"string\" },", StringComparison.Ordinal));

        var findings = new ContractComparer().Compare(baseline, Json(Head)).Findings;

        findings.Single(f => f.Rule == RuleCatalog.TypeChanged).Position.ShouldBe(new SourcePosition(25, 11));
        findings.Single(f => f.Rule == RuleCatalog.PropertyRemoved).Position.ShouldBe(new SourcePosition(21, 7));
    }

    [Test]
    public void A_removed_response_points_at_its_operation_and_a_removed_operation_has_no_position()
    {
        var head = Json(TestSpec.Document("""{ "/a": { "get": { "responses": {} } } }"""));
        var baseline = Json(TestSpec.Document("""{ "/a": { "get": { "responses": { "200": { "description": "d" } } } }, "/b": { "get": { "responses": {} } } }"""));

        var findings = new ContractComparer().Compare(baseline, head).Findings;

        findings.Single(f => f.Rule == RuleCatalog.SuccessResponseRemoved).Position.ShouldBe(head.Operations[0].Position);
        findings.Single(f => f.Rule == RuleCatalog.OperationRemoved).Position.ShouldBeNull();
    }

    [Test]
    public void Sarif_console_and_junit_reports_use_the_position()
    {
        var finding = new Finding(RuleCatalog.ParameterRemoved, "GET /pets", "parameter 'q' (query)", "Parameter 'q' (query) was removed.")
        {
            Position = new SourcePosition(42, 9),
        };
        var result = new ComparisonResult([finding]);
        var context = new ReportContext("v1.json", "api/v2.json");

        using var sarif = JsonDocument.Parse(new SarifReportRenderer().Render(result, context));
        var region = sarif.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region");
        $"{region.GetProperty("startLine")}:{region.GetProperty("startColumn")}-{region.GetProperty("endLine")}:{region.GetProperty("endColumn")}"
            .ShouldBe("42:9-42:10");

        new ConsoleReportRenderer().Render(result, context).ShouldContain("          at api/v2.json:42:9\n");
        new JUnitReportRenderer().Render(result, context).ShouldContain("(at api/v2.json:42:9)");
        new MarkdownReportRenderer().Render(result, context).ShouldContain("`parameter 'q' (query)`<br>line 42 |");
    }
}
