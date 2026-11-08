using GapWatch.Cli;
using GapWatch.Core.Findings;
using GapWatch.Core.Policy;
using Shouldly;

namespace GapWatch.Tests.Policy;

public sealed class BaselineTests
{
    private static readonly Finding TagRemoved =
        new(RuleCatalog.PropertyRemoved, "GET /pets", "response 200 application/json: $[].tag", "Optional response property 'tag' was removed.");

    private static readonly Finding ApiKeyDropped =
        new(RuleCatalog.SecurityRequirementRemoved, "GET /pets", "security", "Security alternative 'apiKey[]' is no longer accepted.");

    private static readonly Finding BasicDropped =
        new(RuleCatalog.SecurityRequirementRemoved, "GET /pets", "security", "Security alternative 'basic[]' is no longer accepted.");

    private static string Describe(IEnumerable<Finding> findings) => string.Join(" | ", findings.Select(f => f.Message));

    [Test]
    public void A_baseline_accepts_exactly_the_findings_it_was_written_from()
    {
        var baseline = Baseline.From([TagRemoved, ApiKeyDropped], "Adopted on the 2.3 release.");

        var result = baseline.Apply(new ComparisonResult([TagRemoved, ApiKeyDropped]));

        result.Findings.ShouldBeEmpty();
        result.Suppressed.Count.ShouldBe(2);
        result.Suppressed[0].Suppression.Justification.ShouldBe("Adopted on the 2.3 release.");
        result.Suppressed[0].Suppression.IsBaseline.ShouldBeTrue();
        result.UnusedSuppressions.ShouldBeEmpty();
    }

    [Test]
    public void Each_entry_accepts_one_finding_so_a_new_change_at_the_same_place_still_reports()
    {
        // Same rule, operation and location; only the message differs.
        var baseline = Baseline.From([ApiKeyDropped], "j");

        var result = baseline.Apply(new ComparisonResult([BasicDropped, ApiKeyDropped]));

        Describe(result.Findings).ShouldBe(BasicDropped.Message);
        Describe(result.Suppressed.Select(s => s.Finding)).ShouldBe(ApiKeyDropped.Message);
    }

    [Test]
    public void A_reworded_finding_is_still_accepted_by_its_entry()
    {
        var baseline = new Baseline("j", [new BaselineEntry("GW026", "GET /pets", TagRemoved.Location, "Old wording.")]);

        baseline.Apply(new ComparisonResult([TagRemoved])).Findings.ShouldBeEmpty();
    }

    [Test]
    public void Entries_that_match_nothing_are_reported_as_unused_baseline_entries()
    {
        var baseline = Baseline.From([TagRemoved, ApiKeyDropped], "j");

        var unused = baseline.Apply(new ComparisonResult([TagRemoved])).UnusedSuppressions.ShouldHaveSingleItem();

        unused.IsBaseline.ShouldBeTrue();
        unused.Describe().ShouldBe("baseline: GW051 GET /pets security");
    }

    [Test]
    public void Findings_suppressed_by_the_configuration_stay_suppressed_with_their_own_justification()
    {
        var configured = new SuppressedFinding(TagRemoved, new Suppression("GW026", null, null, "Agreed with consumers."));

        var result = Baseline.From([ApiKeyDropped], "j").Apply(new ComparisonResult([ApiKeyDropped], [configured]));

        string.Join(",", result.Suppressed.Select(s => s.Suppression.Justification)).ShouldBe("j,Agreed with consumers.");
    }

    [Test]
    public void The_file_round_trips_and_is_written_deterministically()
    {
        var baseline = Baseline.From([TagRemoved, ApiKeyDropped], "Adopted on the 2.3 release.");

        var json = BaselineFile.Serialize(baseline);

        json.ShouldBe(BaselineFile.Serialize(BaselineFile.Parse(json, "b.json")));
        json.ShouldStartWith("{\n  \"version\": 1,\n  \"justification\": \"Adopted on the 2.3 release.\",\n  \"findings\": [\n");
        string.Join(" | ", BaselineFile.Parse(json, "b.json").Entries).ShouldBe(string.Join(" | ", baseline.Entries));
    }

    [Test]
    [Arguments("""{ "justification": "j", "findings": [] }""", "$.version is missing")]
    [Arguments("""{ "version": 2, "justification": "j", "findings": [] }""", "baseline version 2 is not supported")]
    [Arguments("""{ "version": 1, "justification": " ", "findings": [] }""", "$.justification is missing or blank")]
    [Arguments("""{ "version": 1, "justification": "j", "findings": [ { "rule": "GW026" } ] }""", "$.findings[0] needs 'rule', 'operation' and 'location'")]
    [Arguments("""{ "version": 1, "justification": "j", "findings": [ { "rule": "GW026", "operation": "o", "location": "l", "x": 1 } ] }""", "unknown property '$.findings[0].x'")]
    [Arguments("""{ "version": 1, "justification": "j", "extra": true }""", "unknown property 'extra'")]
    public void Invalid_baseline_files_are_rejected_with_the_reason(string json, string expected)
    {
        Should.Throw<ConfigurationException>(() => BaselineFile.Parse(json, "b.json")).Message.ShouldContain(expected);
    }
}
