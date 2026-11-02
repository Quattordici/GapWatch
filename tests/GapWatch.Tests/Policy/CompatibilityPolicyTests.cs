using GapWatch.Core.Findings;
using GapWatch.Core.Policy;
using Shouldly;

namespace GapWatch.Tests.Policy;

public sealed class CompatibilityPolicyTests
{
    private static readonly Finding Removed =
        new(RuleCatalog.RequiredPropertyRemoved, "GET /pets", "response 200 application/json: $[].nickname", "Required property 'nickname' was removed.");

    private static readonly Finding RenamedId =
        new(RuleCatalog.OperationIdChanged, "GET /pets", "operation", "operationId changed from 'ListPets' to 'GetPets'.");

    private static readonly Finding Added =
        new(RuleCatalog.OperationAdded, "POST /owners", "operation", "Operation 'POST /owners' was added.");

    private static ComparisonResult Raw() => new(FindingOrder.Sort([Removed, RenamedId, Added]));

    private static CompatibilityPolicy Policy(
        Dictionary<string, Severity>? overrides = null,
        string[]? disabled = null,
        params Suppression[] suppressions) =>
        new(
            overrides ?? new Dictionary<string, Severity>(StringComparer.Ordinal),
            new HashSet<string>(disabled ?? [], StringComparer.Ordinal),
            suppressions);

    private static string Ids(IEnumerable<Finding> findings) => string.Join(",", findings.Select(f => f.Rule.Id));

    [Test]
    public void Default_policy_changes_nothing()
    {
        var result = CompatibilityPolicy.Default.Apply(Raw());

        Ids(result.Findings).ShouldBe("GW025,GW004,GW003");
        result.Suppressed.ShouldBeEmpty();
        result.UnusedSuppressions.ShouldBeEmpty();
    }

    [Test]
    public void Severity_override_changes_the_severity_and_the_order()
    {
        var policy = Policy(new Dictionary<string, Severity> { ["GW004"] = Severity.Breaking, ["GW025"] = Severity.Info });

        var result = policy.Apply(Raw());

        // Both info findings are then ordered by operation: GET /pets before POST /owners.
        Ids(result.Findings).ShouldBe("GW004,GW025,GW003");
        result.Findings[0].Severity.ShouldBe(Severity.Breaking);
        result.Findings[0].Rule.DefaultSeverity.ShouldBe(Severity.Warning);
        result.HasAtLeast(Severity.Breaking).ShouldBeTrue();
    }

    [Test]
    public void Downgrading_the_only_breaking_rule_clears_the_breaking_threshold()
    {
        var result = Policy(new Dictionary<string, Severity> { ["GW025"] = Severity.Warning }).Apply(Raw());

        result.HasAtLeast(Severity.Breaking).ShouldBeFalse();
        result.Count(Severity.Warning).ShouldBe(2);
    }

    [Test]
    public void Disabled_rules_are_dropped_and_not_listed_as_suppressed()
    {
        var result = Policy(disabled: ["GW003"]).Apply(Raw());

        Ids(result.Findings).ShouldBe("GW025,GW004");
        result.Suppressed.ShouldBeEmpty();
    }

    [Test]
    public void Suppression_by_rule_operation_and_location_moves_the_finding_aside()
    {
        var suppression = new Suppression("GW025", "GET /pets", "response 200 application/json: $[].nickname", "Never populated.");

        var result = Policy(suppressions: suppression).Apply(Raw());

        Ids(result.Findings).ShouldBe("GW004,GW003");
        result.Suppressed.ShouldHaveSingleItem().Finding.ShouldBe(Removed);
        result.Suppressed[0].Suppression.ShouldBe(suppression);
        result.HasAtLeast(Severity.Breaking).ShouldBeFalse();
        result.Count(Severity.Breaking).ShouldBe(0);
        result.UnusedSuppressions.ShouldBeEmpty();
    }

    [Test]
    public void Suppression_without_operation_or_location_covers_every_finding_of_the_rule()
    {
        var result = Policy(suppressions: new Suppression("GW004", null, null, "Clients are regenerated on release.")).Apply(Raw());

        Ids(result.Suppressed.Select(s => s.Finding)).ShouldBe("GW004");
    }

    [Test]
    public void Wildcards_match_any_run_of_characters()
    {
        var suppression = new Suppression("GW025", "GET /p*", "*.nickname", "Never populated.");

        Policy(suppressions: suppression).Apply(Raw()).Suppressed.ShouldHaveSingleItem();
    }

    [Test]
    public void Suppression_for_another_rule_operation_or_location_does_not_match()
    {
        new Suppression("GW026", "GET /pets", null, "x").Matches(Removed).ShouldBeFalse();
        new Suppression("GW025", "GET /pets/{id}", null, "x").Matches(Removed).ShouldBeFalse();
        new Suppression("GW025", "get /pets", null, "x").Matches(Removed).ShouldBeFalse();
        new Suppression("GW025", null, "$[].nickname", "x").Matches(Removed).ShouldBeFalse();
    }

    [Test]
    public void Suppressions_that_match_nothing_are_reported_as_unused()
    {
        var stale = new Suppression("GW001", "GET /owners", null, "Retired in 2025.");

        var result = Policy(suppressions: stale).Apply(Raw());

        result.UnusedSuppressions.ShouldHaveSingleItem().ShouldBe(stale);
        Ids(result.Findings).ShouldBe("GW025,GW004,GW003");
    }

    [Test]
    public void Suppressions_see_the_overridden_severity()
    {
        var policy = Policy(
            new Dictionary<string, Severity> { ["GW025"] = Severity.Warning },
            suppressions: new Suppression("GW025", null, null, "Accepted."));

        policy.Apply(Raw()).Suppressed.ShouldHaveSingleItem().Finding.Severity.ShouldBe(Severity.Warning);
    }

    [Test]
    public void Validation_reports_unknown_ids_blank_justifications_and_contradictions()
    {
        var policy = Policy(
            new Dictionary<string, Severity> { ["GW999"] = Severity.Info, ["GW003"] = Severity.Info },
            ["GW003"],
            new Suppression("GW025", null, null, "  "),
            new Suppression("BW001", null, null, "ok"));

        var problems = string.Join("\n", policy.Validate());

        problems.ShouldContain("Unknown rule id 'GW999'");
        problems.ShouldContain("Rule 'GW003' is both disabled and given a severity");
        problems.ShouldContain("Suppression #1 (GW025 *) has no justification");
        problems.ShouldContain("Suppression #2 (BW001 *) refers to unknown rule id 'BW001'");
        Should.Throw<ArgumentException>(() => policy.Apply(Raw()));
    }

    [Test]
    public void A_valid_policy_has_no_problems()
    {
        Policy(
                new Dictionary<string, Severity> { ["GW004"] = Severity.Breaking },
                ["GW003"],
                new Suppression("GW025", "GET /pets", null, "Agreed with consumers."))
            .Validate()
            .ShouldBeEmpty();
    }
}
