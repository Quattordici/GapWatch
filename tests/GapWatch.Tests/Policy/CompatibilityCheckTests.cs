using GapWatch.Core;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Core.Policy;
using Shouldly;

namespace GapWatch.Tests.Policy;

public sealed class CompatibilityCheckTests
{
    private const string NoContent = "\"responses\": { \"204\": { \"description\": \"d\" } }";

    /// <summary>Version 1 of <c>GET /v1/pets</c> is removed; version 2 stays, so GW060 replaces the GW001.</summary>
    private static (ApiContract Base, ApiContract Head) RemovedVersion() =>
        (TestSpec.Parse(TestSpec.Document($$"""{ "/v1/pets": { "get": { {{NoContent}} } }, "/v2/pets": { "get": { {{NoContent}} } } }""")),
         TestSpec.Parse(TestSpec.Document($$"""{ "/v2/pets": { "get": { {{NoContent}} } } }""")));

    private static GapWatchConfiguration With(
        IReadOnlyDictionary<string, Severity>? overrides = null, params Suppression[] suppressions) =>
        GapWatchConfiguration.Default with
        {
            Policy = new CompatibilityPolicy(
                overrides ?? new Dictionary<string, Severity>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                suppressions),
        };

    [Test]
    public void Version_findings_are_produced_before_the_policy_so_they_can_be_suppressed()
    {
        var (before, after) = RemovedVersion();

        var result = CompatibilityCheck.Run(before, after, With(suppressions: new Suppression("GW060", null, null, "Retired with notice."))).Result;

        result.Findings.ShouldBeEmpty();
        result.Suppressed.ShouldHaveSingleItem().Finding.Rule.Id.ShouldBe("GW060");
    }

    [Test]
    public void The_baseline_accepts_findings_with_their_configured_severity_and_before_baseline_keeps_them_active()
    {
        var (before, after) = RemovedVersion();
        var configuration = With(new Dictionary<string, Severity>(StringComparer.Ordinal) { ["GW060"] = Severity.Warning });
        var accepted = Baseline.From([new Finding(RuleCatalog.ApiVersionRemoved, "GET /v1/pets", FindingLocation.Operation.Instance, "m")], "Known.");

        var check = CompatibilityCheck.Run(before, after, configuration, accepted);

        check.Result.Findings.ShouldBeEmpty();
        check.Result.Suppressed.ShouldHaveSingleItem().Finding.Severity.ShouldBe(Severity.Warning);
        check.BeforeBaseline.Findings.ShouldHaveSingleItem().Severity.ShouldBe(Severity.Warning);
    }

    [Test]
    public void Suppressions_expire_only_when_a_date_is_given()
    {
        var (before, after) = RemovedVersion();
        var configuration = With(suppressions: new Suppression("GW060", null, null, "Until June.") { Expires = new DateOnly(2026, 6, 1) });

        CompatibilityCheck.Run(before, after, configuration).Result.Findings.ShouldBeEmpty();
        CompatibilityCheck.Run(before, after, configuration, today: new DateOnly(2026, 6, 1)).Result.Findings
            .ShouldHaveSingleItem().Rule.Id.ShouldBe("GW060");
    }
}
