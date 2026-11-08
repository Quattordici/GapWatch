using GapWatch.Cli;
using GapWatch.Core.Findings;
using Shouldly;

namespace GapWatch.Tests.Policy;

public sealed class ConfigFileTests
{
    private static string ErrorFor(string json) =>
        Should.Throw<ConfigurationException>(() => ConfigFile.Parse(json, "gapwatch.json")).Message;

    [Test]
    public void Reads_overrides_disabled_rules_and_suppressions()
    {
        var policy = ConfigFile.Parse(
            """
            {
              // Comments and trailing commas are allowed.
              "rules": { "GW004": "Breaking", "GW003": "off", "GW024": "info", "GW012": "warning", },
              "suppressions": [
                { "rule": "GW025", "operation": "GET /pets", "location": "*.nickname", "justification": "Never populated." },
                { "rule": "GW004", "justification": "Clients regenerate on release." },
              ],
            }
            """,
            "gapwatch.json");

        policy.SeverityOverrides["GW004"].ShouldBe(Severity.Breaking);
        policy.SeverityOverrides["GW024"].ShouldBe(Severity.Info);
        policy.SeverityOverrides["GW012"].ShouldBe(Severity.Warning);
        string.Join(",", policy.DisabledRules).ShouldBe("GW003");
        policy.Suppressions.Count.ShouldBe(2);
        policy.Suppressions[0].ShouldBe(new GapWatch.Core.Policy.Suppression("GW025", "GET /pets", "*.nickname", "Never populated."));
        policy.Suppressions[1].Operation.ShouldBeNull();
        policy.Suppressions[1].Location.ShouldBeNull();
    }

    [Test]
    public void An_empty_object_is_the_default_policy()
    {
        var policy = ConfigFile.Parse("{}", "gapwatch.json");

        policy.SeverityOverrides.ShouldBeEmpty();
        policy.DisabledRules.ShouldBeEmpty();
        policy.Suppressions.ShouldBeEmpty();
    }

    [Test]
    public void Suppressions_without_a_justification_are_rejected()
    {
        ErrorFor("""{ "suppressions": [ { "rule": "GW025" } ] }""").ShouldContain("has no justification");
        ErrorFor("""{ "suppressions": [ { "rule": "GW025", "justification": "" } ] }""").ShouldContain("has no justification");
    }

    [Test]
    public void Suppressions_without_a_rule_are_rejected()
    {
        ErrorFor("""{ "suppressions": [ { "justification": "x" } ] }""").ShouldContain("$.suppressions[0] needs a 'rule'");
    }

    [Test]
    public void Unknown_rule_ids_are_rejected()
    {
        ErrorFor("""{ "rules": { "GW999": "off" } }""").ShouldContain("Unknown rule id 'GW999'");
    }

    [Test]
    public void Unknown_severities_are_rejected()
    {
        ErrorFor("""{ "rules": { "GW004": "error" } }""").ShouldContain("$.rules.GW004 has value 'error'");
    }

    [Test]
    public void Unknown_properties_are_rejected_so_typos_do_not_go_unnoticed()
    {
        ErrorFor("""{ "supressions": [] }""").ShouldContain("unknown property 'supressions'");
        ErrorFor("""{ "suppressions": [ { "rule": "GW025", "justifcation": "x" } ] }""")
            .ShouldContain("unknown property '$.suppressions[0].justifcation'");
    }

    [Test]
    public void Wrong_value_kinds_are_rejected_with_the_json_path()
    {
        ErrorFor("[]").ShouldContain("$ must be an object, not array");
        ErrorFor("""{ "rules": [] }""").ShouldContain("$.rules must be an object");
        ErrorFor("""{ "suppressions": {} }""").ShouldContain("$.suppressions must be an array");
        ErrorFor("""{ "suppressions": [ { "rule": 25 } ] }""").ShouldContain("$.suppressions[0].rule must be a string, not number");
    }

    [Test]
    public void Malformed_json_is_rejected()
    {
        ErrorFor("{ nope").ShouldStartWith("gapwatch.json is not valid JSON");
    }

    [Test]
    public void Missing_files_are_rejected()
    {
        Should.Throw<ConfigurationException>(() => ConfigFile.Load("/definitely/not/gapwatch.json"))
            .Message.ShouldContain("Configuration file not found");
    }

    [Test]
    public void Versioning_defaults_to_asp_versioning_and_can_be_customized_or_turned_off()
    {
        var defaults = ConfigFile.ParseConfiguration("{}", "gapwatch.json").Versioning;
        defaults.PathSegment!.ToString().ShouldBe(GapWatch.Core.Comparison.VersioningOptions.DefaultPathSegment);
        string.Join(",", defaults.Parameters).ShouldBe("api-version");
        defaults.MediaTypeParameter.ShouldBe("v");

        var custom = ConfigFile.ParseConfiguration(
            """{ "versioning": { "pathSegment": "^\\d{4}-\\d{2}-\\d{2}$", "parameters": ["x-api-version", "api-version"], "mediaTypeParameter": null } }""",
            "gapwatch.json").Versioning;
        custom.PathSegment!.IsMatch("2026-01-01").ShouldBeTrue();
        string.Join(",", custom.Parameters).ShouldBe("x-api-version,api-version");
        custom.MediaTypeParameter.ShouldBeNull();
        custom.Enabled.ShouldBeTrue();

        ConfigFile.ParseConfiguration("""{ "versioning": false }""", "gapwatch.json").Versioning.Enabled.ShouldBeFalse();
    }

    [Test]
    public void Invalid_versioning_settings_are_errors()
    {
        ErrorFor("""{ "versioning": { "pathSegment": "(" } }""").ShouldContain("$.versioning.pathSegment is not a valid regular expression");
        ErrorFor("""{ "versioning": { "header": "x" } }""").ShouldContain("unknown property '$.versioning.header'");
        ErrorFor("""{ "versioning": { "parameters": "api-version" } }""").ShouldContain("$.versioning.parameters must be an array");
        ErrorFor("""{ "versioning": true }""").ShouldContain("$.versioning must be an object");
    }
}
