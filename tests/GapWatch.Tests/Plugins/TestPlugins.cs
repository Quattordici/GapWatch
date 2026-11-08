using GapWatch.Core.Findings;
using GapWatch.Sdk;

namespace GapWatch.Tests.Plugins;

/// <summary>A plugin whose prefix, rules and check are given by the test. Each test builds its own instance.</summary>
internal sealed class DelegatePlugin(
    string prefix,
    IReadOnlyList<RuleDescriptor> rules,
    Func<OperationPair, RuleContext, IEnumerable<PluginFinding>>? check = null) : IRulePlugin
{
    public string Prefix { get; } = prefix;

    public IReadOnlyList<RuleDescriptor> Rules { get; } = rules;

    public IEnumerable<PluginFinding> Check(OperationPair pair, RuleContext context) => check?.Invoke(pair, context) ?? [];
}

/// <summary>The rule from the design: a new operation must require authentication.</summary>
internal static class Acme
{
    public static readonly RuleDescriptor NewOperationWithoutSecurity = new(
        "ACME001", nameof(NewOperationWithoutSecurity), Severity.Breaking, "New operations must require authentication.")
    {
        HelpUri = new Uri("https://example.com/rules/ACME001"),
    };

    public static IRulePlugin Plugin() => new DelegatePlugin("ACME", [NewOperationWithoutSecurity], (pair, _) =>
        pair is { Baseline: null, Head: { } head } && (head.Security.Count == 0 || head.Security.Contains("none"))
            ? [new PluginFinding("ACME001", FindingLocation.Operation.Instance, "New operations must require authentication.")]
            : []);

    public static RuleSet Rules() => RuleSet.With([Plugin()]);
}
