using GapWatch.Core;
using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Core.Policy;
using Shouldly;

namespace GapWatch.Tests.Rules;

public sealed class ApiVersionRuleTests
{
    private const string NoContent = "\"responses\": { \"204\": { \"description\": \"d\" } }";
    private const string Deprecated = "\"deprecated\": true, \"responses\": { \"204\": { \"description\": \"d\" } }";

    private static ComparisonResult Analyze(string before, string after, VersioningOptions? options = null)
    {
        var baseline = TestSpec.Parse(before);
        var head = TestSpec.Parse(after);
        return Check(baseline, head, options ?? VersioningOptions.Default);
    }

    /// <summary>A document with <c>GET</c> on each path, all with the same operation body.</summary>
    private static string Gets(string body, params string[] paths) =>
        TestSpec.Document("{" + string.Join(",", paths.Select(p => $$"""
            "{{p}}": { "get": { {{body}} } }
            """)) + "}");

    private static string Paths(params (string Path, string Body)[] operations) =>
        TestSpec.Document("{" + string.Join(",", operations.Select(o => $$"""
            "{{o.Path}}": { "get": { {{o.Body}} } }
            """)) + "}");

    /// <summary>GET /pets with an <c>api-version</c> query parameter limited to <paramref name="versions"/>.</summary>
    private static string QueryVersions(params string[] versions) =>
        TestSpec.Operation("get", "/pets", $$"""
            "parameters": [ { "name": "api-version", "in": "query", "required": true,
                              "schema": { "type": "string", "enum": [{{string.Join(",", versions.Select(v => $"\"{v}\""))}}] } } ],
            {{NoContent}}
            """);

    /// <summary>POST /pets accepting JSON in the given <c>v</c> media type versions.</summary>
    private static string MediaTypeVersions(params string[] versions) =>
        TestSpec.Operation("post", "/pets", $$"""
            "requestBody": { "content": { {{string.Join(",", versions.Select(v => $"\"application/json;v={v}\": {{ \"schema\": {{ \"type\": \"object\" }} }}"))}} } },
            {{NoContent}}
            """);

    // --- GW060 ApiVersionRemoved ----------------------------------------------------------------------------------

    [Test]
    public void Removing_every_operation_of_a_path_version_is_GW060_instead_of_GW001()
    {
        var before = Gets(NoContent, "/v1/pets", "/v1/pets/{id}", "/v2/pets");
        var after = Gets(NoContent, "/v2/pets");

        var result = Analyze(before, after);

        result.RuleIds().ShouldBe("GW060,GW060");
        string.Join(",", result.Findings.Select(f => f.Operation)).ShouldBe("GET /v1/pets,GET /v1/pets/{id}");
        result.Findings[0].Message.ShouldBe("API version 'v1' was removed without being deprecated first (2 operations).");
        result.Findings[0].Severity.ShouldBe(Severity.Breaking);
        result.Findings[0].ApiVersion.ShouldBe("v1");
    }

    [Test]
    public void Removing_one_operation_of_a_version_that_stays_is_still_GW001()
    {
        var before = Gets(NoContent, "/v1/pets", "/v1/owners");
        var after = Gets(NoContent, "/v1/pets");

        Analyze(before, after).RuleIds().ShouldBe("GW001");
    }

    [Test]
    public void Dropping_a_version_from_the_api_version_enum_everywhere_is_GW060_instead_of_GW023()
    {
        var result = Analyze(QueryVersions("1.0", "2.0"), QueryVersions("2.0"));

        result.RuleIds().ShouldBe("GW060");
        result.Findings[0].Operation.ShouldBe("GET /pets");
        result.Findings[0].Message.ShouldBe("API version '1.0' was removed without being deprecated first (1 operation).");
    }

    [Test]
    public void Dropping_a_version_from_one_operation_while_another_keeps_it_is_still_GW023()
    {
        static string Body(string versions) =>
            $$"""
            "parameters": [ { "name": "api-version", "in": "query", "schema": { "type": "string", "enum": [{{versions}}] } } ], {{NoContent}}
            """;
        var before = Paths(("/pets", Body("\"1.0\", \"2.0\"")), ("/owners", Body("\"1.0\", \"2.0\"")));
        var after = Paths(("/pets", Body("\"2.0\"")), ("/owners", Body("\"1.0\", \"2.0\"")));

        var result = Analyze(before, after);

        result.RuleIds().ShouldBe("GW023");
        result.Findings[0].Operation.ShouldBe("GET /pets");
    }

    [Test]
    public void Dropping_a_media_type_version_is_GW060_instead_of_GW043()
    {
        var result = Analyze(MediaTypeVersions("1.0", "2.0"), MediaTypeVersions("2.0"));

        result.RuleIds().ShouldBe("GW060");
        result.Findings[0].Message.ShouldBe("API version '1.0' was removed without being deprecated first (1 operation).");
    }

    // --- GW061 DeprecatedApiVersionRemoved ------------------------------------------------------------------------

    [Test]
    public void Removing_a_version_that_was_deprecated_is_a_GW061_warning()
    {
        var before = Paths(("/v1/pets", Deprecated), ("/v1/owners", Deprecated), ("/v2/pets", NoContent));
        var after = Gets(NoContent, "/v2/pets");

        var result = Analyze(before, after);

        result.RuleIds().ShouldBe("GW061,GW061");
        result.Findings[0].Severity.ShouldBe(Severity.Warning);
        result.Findings[0].Message.ShouldBe("API version 'v1' was removed; it was deprecated in the base contract (2 operations).");
    }

    [Test]
    public void Removing_a_version_that_was_only_partly_deprecated_is_GW060()
    {
        var before = Paths(("/v1/pets", Deprecated), ("/v1/owners", NoContent), ("/v2/pets", NoContent));
        var after = Gets(NoContent, "/v2/pets");

        Analyze(before, after).RuleIds().ShouldBe("GW060,GW060");
    }

    // --- GW062 ApiVersionAdded ------------------------------------------------------------------------------------

    [Test]
    public void Adding_a_path_version_is_GW062_instead_of_GW003()
    {
        var before = Gets(NoContent, "/v1/pets");
        var after = Gets(NoContent, "/v1/pets", "/v2/pets", "/v2/owners");

        var result = Analyze(before, after);

        result.RuleIds().ShouldBe("GW062,GW062");
        result.Findings[0].Severity.ShouldBe(Severity.Info);
        result.Findings[0].Message.ShouldBe("API version 'v2' was added (2 operations).");
    }

    [Test]
    public void Adding_an_operation_to_an_existing_version_is_still_GW003()
    {
        var before = Gets(NoContent, "/v1/pets");
        var after = Gets(NoContent, "/v1/pets", "/v1/owners");

        Analyze(before, after).RuleIds().ShouldBe("GW003");
    }

    [Test]
    public void Adding_a_value_to_the_api_version_enum_is_GW062_and_nothing_else()
    {
        Analyze(QueryVersions("1.0"), QueryVersions("1.0", "2.0")).RuleIds().ShouldBe("GW062");
    }

    /// <summary>GET /pets with an <c>api-version</c> query parameter that takes any string.</summary>
    private static readonly string AnyQueryVersion = TestSpec.Operation("get", "/pets", $$"""
        "parameters": [ { "name": "api-version", "in": "query", "required": true, "schema": { "type": "string", "example": "2.0" } } ],
        {{NoContent}}
        """);

    [Test]
    public void Declaring_the_versions_a_document_already_served_is_neither_GW023_nor_GW062()
    {
        // Asp.Versioning 10 added the enum to documents that used to leave api-version free.
        Analyze(AnyQueryVersion, QueryVersions("2.0")).Findings.ShouldBeEmpty();
        Analyze(AnyQueryVersion, QueryVersions("1.0", "2.0")).Findings.ShouldBeEmpty();
    }

    [Test]
    public void In_a_version_document_an_enum_of_its_own_version_is_not_GW023_and_another_version_is()
    {
        static NamedContract Document(string path, string version, string spec) =>
            new(path, TestSpec.Parse(spec.Replace("\"version\": \"1\"", $"\"version\": \"{version}\"", StringComparison.Ordinal)));

        static ComparisonResult Run(string headEnum)
        {
            var (baseline, head) = VersionedDocuments.Combine(
                [Document("base/v1.json", "1.0", AnyQueryVersion)],
                [Document("head/v1.json", "1.0", QueryVersions(headEnum))]);
            return Check(baseline, head, VersioningOptions.Default);
        }

        Run("1.0").Findings.ShouldBeEmpty();
        Run("2.0").RuleIds().ShouldBe("GW023");
    }

    // --- GW063 ApiVersionDeprecated -------------------------------------------------------------------------------

    [Test]
    public void Deprecating_every_operation_of_a_version_is_GW063_instead_of_GW002()
    {
        var before = Gets(NoContent, "/v1/pets", "/v1/owners", "/v2/pets");
        var after = Paths(("/v1/pets", Deprecated), ("/v1/owners", Deprecated), ("/v2/pets", NoContent));

        var result = Analyze(before, after);

        result.RuleIds().ShouldBe("GW063,GW063");
        result.Findings[0].Message.ShouldBe("API version 'v1' was marked deprecated (2 operations).");
    }

    [Test]
    public void Deprecating_some_operations_of_a_version_is_still_GW002()
    {
        var before = Gets(NoContent, "/v1/pets", "/v1/owners");
        var after = Paths(("/v1/pets", Deprecated), ("/v1/owners", NoContent));

        Analyze(before, after).RuleIds().ShouldBe("GW002");
    }

    // --- Detection and configuration ------------------------------------------------------------------------------

    [Test]
    public void Unversioned_operations_keep_the_operation_rules()
    {
        Analyze(Gets(NoContent, "/pets", "/owners"), Gets(NoContent, "/pets")).RuleIds().ShouldBe("GW001");
    }

    [Test]
    public void Disabled_versioning_keeps_the_operation_rules()
    {
        var before = Gets(NoContent, "/v1/pets", "/v2/pets");
        var after = Gets(NoContent, "/v2/pets");

        Analyze(before, after, VersioningOptions.Disabled).RuleIds().ShouldBe("GW001");
    }

    [Test]
    public void A_custom_path_pattern_and_parameter_name_are_used()
    {
        var options = VersioningOptions.Default with
        {
            PathSegment = VersioningOptions.PathPattern(@"^\d{4}-\d{2}-\d{2}$"),
        };

        var result = Analyze(Gets(NoContent, "/2025-01-01/pets", "/2026-01-01/pets"), Gets(NoContent, "/2026-01-01/pets"), options);

        result.RuleIds().ShouldBe("GW060");
        result.Findings[0].ApiVersion.ShouldBe("2025-01-01");
    }

    [Test]
    public void Versions_compare_without_v_prefix_case_and_trailing_zero()
    {
        string.Join(",", new[] { "v1", "V1.0", "1", "1.0.0", "2.10", "2026-01-01" }.Select(ApiVersionAnalyzer.Canonical))
            .ShouldBe("1,1,1,1,2.10,2026-01-01");
    }

    [Test]
    public void Version_findings_group_by_version()
    {
        var result = Analyze(Gets(NoContent, "/v1/pets", "/v1/owners", "/v2/pets"), Gets(NoContent, "/v2/pets"));

        var group = FindingGroup.Group(result.Findings).ShouldHaveSingleItem();
        group.Members.Count.ShouldBe(2);
        group.First.GroupLabel.ShouldBe("API version v1");
    }

    // --- Multi-document mode --------------------------------------------------------------------------------------

    private static NamedContract Doc(string path, string version, params string[] paths) =>
        new(path, TestSpec.Parse(Gets(NoContent, paths).Replace("\"version\": \"1\"", $"\"version\": \"{version}\"", StringComparison.Ordinal)));

    private static ComparisonResult AnalyzeDocuments(IReadOnlyList<NamedContract> before, IReadOnlyList<NamedContract> after)
    {
        var (baseline, head) = VersionedDocuments.Combine(before, after);
        return Check(baseline, head, VersioningOptions.Disabled);
    }

    private static ComparisonResult Check(ApiContract baseline, ApiContract head, VersioningOptions versioning) =>
        CompatibilityCheck.Run(baseline, head, GapWatchConfiguration.Default with { Versioning = versioning }).Result;

    [Test]
    public void A_version_document_missing_from_the_head_is_GW060_for_its_operations()
    {
        var result = AnalyzeDocuments(
            [Doc("base/v1.json", "1.0", "/pets", "/owners"), Doc("base/v2.json", "2.0", "/pets")],
            [Doc("head/v2.json", "2.0", "/pets")]);

        result.RuleIds().ShouldBe("GW060,GW060");
        string.Join(",", result.Findings.Select(f => f.Operation)).ShouldBe("GET /owners [1.0],GET /pets [1.0]");
    }

    [Test]
    public void A_new_version_document_is_GW062_and_changes_inside_a_version_are_compared_per_document()
    {
        var result = AnalyzeDocuments(
            [Doc("base/v1.json", "1.0", "/pets", "/owners")],
            [Doc("head/v1.json", "1.0", "/pets"), Doc("head/v2.json", "2.0", "/pets")]);

        result.RuleIds().ShouldBe("GW001,GW062");
        result.Findings.Single(f => f.Rule.Id == "GW001").Operation.ShouldBe("GET /owners [1.0]");
        var added = result.Findings.Single(f => f.Rule.Id == "GW062");
        added.Operation.ShouldBe("GET /pets [2.0]");
        added.Document.ShouldBe("head/v2.json");
    }

    [Test]
    public void Documents_pair_by_file_name_when_info_version_is_not_unique()
    {
        var result = AnalyzeDocuments(
            [Doc("base/v1.json", "1", "/pets"), Doc("base/v2.json", "1", "/pets")],
            [Doc("head/v1.json", "1", "/pets"), Doc("head/v2.json", "1", "/pets")]);

        result.Findings.ShouldBeEmpty();
        VersionedDocuments.Combine([Doc("base/v1.json", "1", "/pets")], [Doc("head/v1.json", "1", "/pets")])
            .Head.Operations[0].Display.ShouldBe("GET /pets [1]");
    }
}
