using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Roslyn;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>The project's <c>&lt;Nullable&gt;</c> setting, and what an oblivious reference type means in the contract.</summary>
public sealed class CSharpNullableContextTests
{
    private const string Api = """
        using Microsoft.AspNetCore.Http;

        public sealed class Pet
        {
            public string Name { get; set; }
            public string Note { get; set; }
        }

        public static class Endpoints
        {
            public static void Map(WebApplication app)
            {
                app.MapGet("/pets", (string status) => Results.Ok(new Pet()));
                app.MapPost("/pets", (Pet pet) => Results.Ok(pet));
                app.MapGet("/pets/{id}", (int id) => new Pet());
            }
        }
        """;

    private static readonly string Enabled = Api
        .Replace("public string Name", "public required string Name", StringComparison.Ordinal)
        .Replace("public string Note", "public string? Note", StringComparison.Ordinal);

    private static ApiContract Read(string source, bool nullable) =>
        CSharpContractReader.FromSources([("Api.cs", source)], nullable);

    private static string Findings(ComparisonResult result) =>
        string.Join(", ", result.Findings.Select(f => $"{f.Rule.Id} {f.Operation} {f.Location}").Order(StringComparer.Ordinal));

    [Test]
    public void Oblivious_reference_types_are_optional_in_requests_and_nullable_inside_bodies()
    {
        var contract = Read(Api, nullable: false);
        var list = contract.Operations.Single(o => o.Display == "GET /pets");
        var create = contract.Operations.Single(o => o.Display == "POST /pets");

        list.Parameters.Single().Required.ShouldBeFalse();
        list.Parameters.Single().Schema.Nullable.ShouldBeFalse();
        create.RequestBody!.Required.ShouldBeFalse();
        create.RequestBody.Content["application/json"].Nullable.ShouldBeFalse();
        contract.Operations.Single(o => o.Display == "GET /pets/{id}").Responses.Single().Content["application/json"]
            .Properties["name"].Schema.Nullable.ShouldBeTrue();
    }

    [Test]
    public void Turning_nullable_on_breaks_requests_that_stop_accepting_null_and_nothing_in_responses()
    {
        var result = new ContractComparer().Compare(Read(Api, nullable: false), Read(Enabled, nullable: true));

        Findings(result).ShouldBe(
            "GW011 GET /pets parameter 'status' (query), " +
            "GW022 POST /pets request body (application/json): $.name, " +
            "GW028 POST /pets request body (application/json): $.name, " +
            "GW041 POST /pets request body");
    }

    [Test]
    public void Nullable_directives_in_a_file_override_the_project_setting()
    {
        var contract = Read("#nullable enable\n" + Enabled, nullable: false);

        contract.Operations.Single(o => o.Display == "GET /pets").Parameters.Single().Required.ShouldBeTrue();
    }

    [Test]
    public void The_project_file_wins_over_directory_build_props()
    {
        const string enable = "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>";
        const string disable = "<Project><PropertyGroup><Nullable>disable</Nullable></PropertyGroup></Project>";
        const string none = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

        ProjectSettings.NullableEnabled(enable, disable).ShouldBeTrue();
        ProjectSettings.NullableEnabled(disable, enable).ShouldBeFalse();
        ProjectSettings.NullableEnabled(none, enable).ShouldBeTrue();
        ProjectSettings.NullableEnabled(none, null).ShouldBeFalse();
        ProjectSettings.NullableEnabled(null, null).ShouldBeTrue();
        ProjectSettings.NullableEnabled(enable.Replace("enable", "annotations", StringComparison.Ordinal), null).ShouldBeTrue();
        ProjectSettings.NullableEnabled(enable.Replace("enable", "warnings", StringComparison.Ordinal), null).ShouldBeFalse();
        ProjectSettings.NullableEnabled("not xml", null).ShouldBeFalse();
    }

    [Test]
    public void The_nearest_project_file_and_props_above_the_source_folder_are_used()
    {
        var root = Path.Combine(Path.GetTempPath(), "gapwatch-nullable-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "src", "Api");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project><PropertyGroup><Nullable>disable</Nullable></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(source, "Api.cs"), Api);

            CSharpContractReader.Load(source).Operations.Single(o => o.Display == "GET /pets").Parameters.Single().Required.ShouldBeFalse();

            File.WriteAllText(Path.Combine(source, "Api.csproj"), "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");

            CSharpContractReader.Load(source).Operations.Single(o => o.Display == "GET /pets").Parameters.Single().Required.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
