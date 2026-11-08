using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Core.Policy;

namespace GapWatch.Core;

/// <summary>The outcome of <see cref="CompatibilityCheck.Run"/>.</summary>
/// <param name="Result">What is reported: every step applied, the baseline last.</param>
/// <param name="BeforeBaseline">
/// The same result before the baseline was applied, with the policy already in effect. It is what a new baseline is
/// written from (<c>--write-baseline</c>).
/// </param>
public sealed record CheckResult(ComparisonResult Result, ComparisonResult BeforeBaseline);

/// <summary>
/// The whole check, in the one order that is correct: compare the contracts, turn per-operation findings about API
/// versions into version findings, apply the configured policy, then the baseline. Plugin rules run with the built-in
/// ones, before the version analyzer.
/// </summary>
/// <remarks>
/// The order matters. The version analyzer runs before the policy, so severity overrides, disabled rules and
/// suppressions see GW060 to GW063. The baseline runs after the policy, so it accepts findings with their configured
/// severity and never sees a disabled rule. Mapping findings to code (<c>CodeMap</c>) is display-only and comes after
/// this, outside Core.
/// </remarks>
public static class CompatibilityCheck
{
    /// <summary>Checks <paramref name="head"/> against <paramref name="baseline"/>, what consumers rely on today.</summary>
    /// <param name="baseline">The contract consumers rely on today.</param>
    /// <param name="head">The proposed change.</param>
    /// <param name="configuration">Policy and versioning options; <see cref="GapWatchConfiguration.Default"/> when there is no file.</param>
    /// <param name="accepted">Findings accepted by a baseline file, or null for none.</param>
    /// <param name="rules">
    /// The built-in rules plus the plugins to run (see <see cref="RuleSet.With"/>); null for the built-in rules alone.
    /// Plugin findings join the built-in ones before the version analyzer, so the policy and the baseline apply to both.
    /// </param>
    /// <param name="today">
    /// The date suppression expiry is checked against. Core reads no clock, so the caller supplies it; null means no
    /// suppression expires.
    /// </param>
    public static CheckResult Run(
        ApiContract baseline,
        ApiContract head,
        GapWatchConfiguration configuration,
        Baseline? accepted = null,
        DateOnly? today = null,
        RuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(configuration);

        rules ??= RuleSet.BuiltIn;

        var result = new ContractComparer().Compare(baseline, head);
        var pluginFindings = PluginRunner.Run(baseline, head, rules);
        if (pluginFindings.Count > 0)
        {
            result = new ComparisonResult(FindingOrder.Sort(result.Findings.Concat(pluginFindings)));
        }

        result = ApiVersionAnalyzer.Apply(baseline, head, result, configuration.Versioning);
        result = (configuration.Policy with { Today = today }).Apply(result, rules);

        return new CheckResult((accepted ?? Baseline.Empty).Apply(result), result);
    }
}
