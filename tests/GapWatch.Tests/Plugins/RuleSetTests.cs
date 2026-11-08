using GapWatch.Core.Findings;
using GapWatch.Reporting;
using GapWatch.Sdk;
using Shouldly;

namespace GapWatch.Tests.Plugins;

public sealed class RuleSetTests
{
    private static RuleDescriptor Rule(string id) => new(id, "Name", Severity.Warning, "Description.");

    private static string Problem(params IRulePlugin[] plugins) =>
        Should.Throw<PluginException>(() => RuleSet.With(plugins)).Message;

    [Test]
    public void The_sdk_references_nothing_but_the_runtime()
    {
        var references = typeof(IRulePlugin).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Where(name => !name.StartsWith("System", StringComparison.Ordinal) && name != "netstandard").ShouldBeEmpty();
    }

    [Test]
    public void The_built_in_set_is_the_rule_catalog()
    {
        string.Join(",", RuleSet.BuiltIn.All.Select(r => r.Id)).ShouldBe(string.Join(",", RuleCatalog.All.Select(r => r.Id)));
        RuleSet.BuiltIn.Plugins.ShouldBeEmpty();
        RuleSet.BuiltIn.PluginOf("GW001").ShouldBeNull();
    }

    [Test]
    public void Plugin_rules_follow_the_built_in_ones_ordered_by_id()
    {
        var rules = RuleSet.With([new DelegatePlugin("ACME", [Rule("ACME002"), Rule("ACME001")])]);

        string.Join(",", rules.All.TakeLast(3).Select(r => r.Id)).ShouldBe("GW063,ACME001,ACME002");
        rules.Contains("ACME002").ShouldBeTrue();
        rules.PluginOf("ACME001").ShouldBe("GapWatch.Tests");
        rules.PluginOf("GW001").ShouldBeNull();
    }

    [Test]
    [Arguments("GW")]
    [Arguments("GWACME")]
    public void Prefixes_starting_with_GW_are_reserved(string prefix)
    {
        Problem(new DelegatePlugin(prefix, [Rule(prefix + "1")]))
            .ShouldBe($"Plugin 'GapWatch.Tests' has prefix '{prefix}'. Prefixes starting with 'GW' are reserved for GapWatch's own rules.");
    }

    [Test]
    [Arguments("A")]
    [Arguments("acme")]
    [Arguments("ACME1")]
    [Arguments("TOOLONGXY")]
    [Arguments("")]
    public void A_prefix_is_two_to_eight_uppercase_letters(string prefix)
    {
        Problem(new DelegatePlugin(prefix, [Rule("ACME001")])).ShouldContain($"has prefix '{prefix}'. A prefix is 2 to 8 uppercase letters");
    }

    [Test]
    [Arguments("ACME")]
    [Arguments("ACMEX001")]
    [Arguments("ACME00001")]
    [Arguments("PAY001")]
    [Arguments("GW001")]
    [Arguments("acme001")]
    public void A_rule_id_is_the_prefix_and_one_to_four_digits(string id)
    {
        Problem(new DelegatePlugin("ACME", [Rule(id)]))
            .ShouldBe($"Plugin 'GapWatch.Tests' declares rule id '{id}'. Its ids are 'ACME' followed by 1 to 4 digits, such as 'ACME001'.");
    }

    [Test]
    public void A_rule_id_is_declared_once()
    {
        Problem(new DelegatePlugin("ACME", [Rule("ACME001"), Rule("ACME001")])).ShouldBe("Plugin 'GapWatch.Tests' declares rule id 'ACME001' twice.");
    }

    [Test]
    public void Two_plugins_cannot_share_a_prefix()
    {
        Problem(new DelegatePlugin("ACME", [Rule("ACME001")]), new DelegatePlugin("ACME", [Rule("ACME002")]))
            .ShouldBe("Plugins 'GapWatch.Tests' and 'GapWatch.Tests' both use prefix 'ACME'. Each plugin needs its own.");
    }

    [Test]
    public void A_plugin_declares_at_least_one_rule()
    {
        Problem(new DelegatePlugin("ACME", [])).ShouldBe("Plugin 'GapWatch.Tests' declares no rules.");
    }

    [Test]
    public void The_rule_list_names_the_plugin_of_a_plugin_rule()
    {
        var text = RuleListRenderer.RenderConsole(Acme.Rules());

        text.ShouldContain("ACME001  BREAKING  NewOperationWithoutSecurity (plugin GapWatch.Tests)\n         New operations must require authentication.\n");
        text.ShouldContain("GW001    BREAKING  OperationRemoved\n");
    }

    [Test]
    public void Without_plugins_the_rule_list_is_unchanged()
    {
        RuleListRenderer.RenderConsole().ShouldStartWith("GW001  BREAKING  OperationRemoved\n       An operation");
        RuleListRenderer.RenderMarkdown().ShouldNotContain("ACME");
    }
}
