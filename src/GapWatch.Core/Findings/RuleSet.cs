using System.Text.RegularExpressions;
using GapWatch.Sdk;

namespace GapWatch.Core.Findings;

/// <summary>A plugin could not be used: its rules are malformed, or it threw or reported an undeclared rule.</summary>
/// <remarks>The CLI reports it with exit code 2, like unreadable input. A check never continues without the plugin.</remarks>
public sealed class PluginException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The rules of one run: the built-in rules, then the rules of every loaded plugin. Configuration is validated against
/// it, and reports list it.
/// </summary>
public sealed partial class RuleSet
{
    private readonly Dictionary<string, string> pluginOf;

    private RuleSet(IReadOnlyList<RuleDescriptor> all, IReadOnlyList<IRulePlugin> plugins, Dictionary<string, string> pluginOf)
    {
        All = all;
        Plugins = plugins;
        this.pluginOf = pluginOf;
    }

    /// <summary>The built-in rules alone, as when no plugin is configured.</summary>
    public static RuleSet BuiltIn { get; } = new(RuleCatalog.All, [], new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Every rule: the built-in ones ordered by id, then each plugin's, in load order and by id.</summary>
    public IReadOnlyList<RuleDescriptor> All { get; }

    /// <summary>The loaded plugins, in load order.</summary>
    public IReadOnlyList<IRulePlugin> Plugins { get; }

    /// <summary>True when <paramref name="ruleId"/> is a built-in or plugin rule.</summary>
    public bool Contains(string ruleId) => All.Any(r => string.Equals(r.Id, ruleId, StringComparison.Ordinal));

    /// <summary>The name of the plugin that declares <paramref name="ruleId"/>, or null for a built-in rule.</summary>
    public string? PluginOf(string ruleId) => pluginOf.GetValueOrDefault(ruleId);

    /// <summary>The name a plugin is reported under: its assembly name.</summary>
    public static string NameOf(IRulePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var type = plugin.GetType();
        return type.Assembly.GetName().Name ?? type.FullName ?? type.Name;
    }

    /// <summary>The built-in rules plus the rules of <paramref name="plugins"/>, after checking every id.</summary>
    /// <exception cref="PluginException">
    /// A prefix or id is malformed, starts with the reserved <c>GW</c>, is repeated, or two plugins share a prefix.
    /// </exception>
    public static RuleSet With(IEnumerable<IRulePlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        var loaded = plugins.ToList();
        var all = new List<RuleDescriptor>(RuleCatalog.All);
        var pluginOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var plugin in loaded)
        {
            var name = NameOf(plugin);
            var prefix = plugin.Prefix;

            if (prefix is null || !PrefixPattern().IsMatch(prefix))
            {
                problems.Add($"Plugin '{name}' has prefix '{prefix}'. A prefix is 2 to 8 uppercase letters (A to Z).");
                continue;
            }

            if (prefix.StartsWith("GW", StringComparison.Ordinal))
            {
                problems.Add($"Plugin '{name}' has prefix '{prefix}'. Prefixes starting with 'GW' are reserved for GapWatch's own rules.");
                continue;
            }

            if (prefixes.TryGetValue(prefix, out var other))
            {
                problems.Add($"Plugins '{other}' and '{name}' both use prefix '{prefix}'. Each plugin needs its own.");
                continue;
            }

            prefixes[prefix] = name;

            var rules = plugin.Rules ?? [];
            if (rules.Count == 0)
            {
                problems.Add($"Plugin '{name}' declares no rules.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                if (rule is null)
                {
                    problems.Add($"Plugin '{name}' declares a null rule.");
                }
                else if (rule.Id is null || !rule.Id.StartsWith(prefix, StringComparison.Ordinal) || !DigitsPattern().IsMatch(rule.Id[prefix.Length..]))
                {
                    problems.Add($"Plugin '{name}' declares rule id '{rule.Id}'. Its ids are '{prefix}' followed by 1 to 4 digits, such as '{prefix}001'.");
                }
                else if (!seen.Add(rule.Id))
                {
                    problems.Add($"Plugin '{name}' declares rule id '{rule.Id}' twice.");
                }
            }

            foreach (var rule in rules.Where(r => r is not null).OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                if (pluginOf.TryAdd(rule.Id, name))
                {
                    all.Add(rule);
                }
            }
        }

        return problems.Count == 0
            ? new RuleSet(all, loaded, pluginOf)
            : throw new PluginException(string.Join(" ", problems));
    }

    [GeneratedRegex(@"^[A-Z]{2,8}\z")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"^[0-9]{1,4}\z")]
    private static partial Regex DigitsPattern();
}
