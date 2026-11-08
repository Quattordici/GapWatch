using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Sdk;

namespace GapWatch.Core.Comparison;

/// <summary>
/// Runs the plugins of a <see cref="RuleSet"/> over every operation pair and turns what they report into ordinary
/// findings, so everything after comparison treats them like built-in ones.
/// </summary>
internal static class PluginRunner
{
    /// <summary>The findings of every plugin, unsorted.</summary>
    /// <exception cref="PluginException">A plugin threw, or reported a rule it did not declare.</exception>
    public static List<Finding> Run(ApiContract baseline, ApiContract head, RuleSet rules)
    {
        var findings = new List<Finding>();
        if (rules.Plugins.Count == 0)
        {
            return findings;
        }

        var context = new RuleContext(baseline, head);
        var pairs = Pairing.OperationPairs(baseline, head).ToList();

        foreach (var plugin in rules.Plugins)
        {
            var name = RuleSet.NameOf(plugin);
            var declared = plugin.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);

            foreach (var pair in pairs)
            {
                List<PluginFinding> reported;
                try
                {
                    reported = (plugin.Check(pair, context) ?? []).ToList();
                }
                catch (Exception ex)
                {
                    throw new PluginException($"Plugin '{name}' failed on operation '{pair.Operation}': {ex.GetType().Name}: {ex.Message}", ex);
                }

                foreach (var finding in reported)
                {
                    findings.Add(ToFinding(finding, pair, declared, name));
                }
            }
        }

        return findings;
    }

    private static Finding ToFinding(PluginFinding? reported, OperationPair pair, Dictionary<string, RuleDescriptor> declared, string plugin)
    {
        if (reported is null || reported.At is null || string.IsNullOrWhiteSpace(reported.Message))
        {
            throw new PluginException(
                $"Plugin '{plugin}' returned an incomplete finding on operation '{pair.Operation}'. Every finding needs a rule id, a location and a message.");
        }

        if (reported.RuleId is null || !declared.TryGetValue(reported.RuleId, out var rule))
        {
            throw new PluginException(
                $"Plugin '{plugin}' reported rule '{reported.RuleId}' on operation '{pair.Operation}', which it does not declare in its Rules.");
        }

        // Positions come from the contract, never from the plugin: the element the location names in the head
        // operation, as for a built-in finding there, and none for a removed operation, which is in no head document.
        var position = pair.Head is { } head ? LocationPositions.Find(head, reported.At) : null;
        return new Finding(rule, pair.Operation, reported.At, reported.Message)
        {
            Position = position,
            Document = position?.File ?? pair.Head?.Document,
        };
    }
}
