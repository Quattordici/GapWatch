using System.Text;
using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.OpenApi;

namespace GapWatch.Tests;

/// <summary>Builds minimal OpenAPI documents and compares them, so each rule test stays a few lines long.</summary>
internal static class TestSpec
{
    public static ApiContract Parse(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new OpenApiJsonReader().Read(stream);
    }

    /// <summary>A full document around the given <c>paths</c> object.</summary>
    public static string Document(string paths, string components = "{}", string topLevel = "") =>
        $$"""
        { "openapi": "3.0.3", "info": { "title": "t", "version": "1" }, {{topLevel}} "paths": {{paths}}, "components": {{components}} }
        """;

    /// <summary>GET /x returning <paramref name="schema"/> as JSON with the given status code.</summary>
    public static string Response(string schema, string status = "200", string components = "{}") =>
        Document(
            $$"""{ "/x": { "get": { "responses": { "{{status}}": { "description": "d", "content": { "application/json": { "schema": {{schema}} } } } } } } }""",
            components);

    /// <summary>POST /x accepting <paramref name="schema"/> as a JSON body.</summary>
    public static string Request(string schema, bool required = true, string components = "{}") =>
        Document(
            $$"""{ "/x": { "post": { "requestBody": { "required": {{(required ? "true" : "false")}}, "content": { "application/json": { "schema": {{schema}} } } }, "responses": { "204": { "description": "d" } } } } }""",
            components);

    /// <summary>GET /x with a single parameter.</summary>
    public static string Parameter(string name, string location, string schema, bool required = false) =>
        Document(
            $$"""{ "/x": { "get": { "parameters": [ { "name": "{{name}}", "in": "{{location}}", "required": {{(required ? "true" : "false")}}, "schema": {{schema}} } ], "responses": { "204": { "description": "d" } } } } }""");

    /// <summary>A single operation with an arbitrary body (parameters, responses, security, ...).</summary>
    public static string Operation(string method, string path, string operationBody = "\"responses\": { \"204\": { \"description\": \"d\" } }", string topLevel = "") =>
        Document($$"""{ "{{path}}": { "{{method}}": { {{operationBody}} } } }""", topLevel: topLevel);

    public static ComparisonResult Diff(string baseline, string head) =>
        new ContractComparer().Compare(Parse(baseline), Parse(head));

    /// <summary>Sorted, comma-joined rule ids of all findings (e.g. <c>GW001,GW003</c>); an order-independent comparison target.</summary>
    public static string RuleIds(this ComparisonResult result) =>
        string.Join(",", result.Findings.Select(f => f.Rule.Id).OrderBy(id => id, StringComparer.Ordinal));

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
