using GapWatch.Core.Findings;
using GapWatch.Core.Model;

namespace GapWatch.Sdk;

/// <summary>
/// A set of custom rules. GapWatch runs every loaded plugin after its own rules, and the findings go through severity
/// overrides, suppressions, baselines and exit codes exactly as built-in findings do.
/// </summary>
/// <remarks>
/// <para>
/// A plugin is a public, non-abstract class with a public parameterless constructor. GapWatch creates one instance per
/// run and calls <see cref="Check"/> once for each operation pair, in a deterministic order, on one thread.
/// </para>
/// <para>
/// Rules never read configuration: severity, <c>off</c> and suppressions for plugin rules are set in
/// <c>gapwatch.json</c> like any other rule. A rule must not depend on the order operations are checked in, and must
/// return the same findings for the same input.
/// </para>
/// <para>
/// Members are never added to this interface in a 1.x release; new capabilities come as new, optional interfaces.
/// </para>
/// </remarks>
public interface IRulePlugin
{
    /// <summary>
    /// The prefix of every rule id: 2 to 8 uppercase ASCII letters, not starting with <c>GW</c>, which GapWatch reserves.
    /// Two loaded plugins cannot share one.
    /// </summary>
    string Prefix { get; }

    /// <summary>
    /// The rules this plugin reports. Each id is <see cref="Prefix"/> followed by 1 to 4 digits, such as <c>ACME001</c>,
    /// and is unique. A finding for a rule not in this list stops the check.
    /// </summary>
    IReadOnlyList<RuleDescriptor> Rules { get; }

    /// <summary>
    /// Checks one operation: present in both contracts, added (<see cref="OperationPair.Baseline"/> is null) or removed
    /// (<see cref="OperationPair.Head"/> is null).
    /// </summary>
    /// <param name="pair">The operation in the baseline and in the head contract.</param>
    /// <param name="context">Both whole contracts, for rules that look across operations.</param>
    /// <returns>The findings, in any order: GapWatch sorts them with every other finding.</returns>
    IEnumerable<PluginFinding> Check(OperationPair pair, RuleContext context);
}

/// <summary>One operation in the baseline and the head contract, paired the way GapWatch pairs operations.</summary>
/// <param name="Baseline">The operation consumers rely on today, or null when it was added.</param>
/// <param name="Head">The operation after the change, or null when it was removed.</param>
/// <remarks>
/// Operations pair by method, path with parameter names erased, and API version, so renaming <c>{id}</c> to
/// <c>{petId}</c> keeps the pair. At least one side is set.
/// </remarks>
public sealed record OperationPair(OperationContract? Baseline, OperationContract? Head)
{
    /// <summary>The operation string findings are reported under: the head's, or the baseline's for a removed operation.</summary>
    public string Operation => (Head ?? Baseline ?? throw new InvalidOperationException("An operation pair has at least one side.")).Display;

    /// <summary>True when the operation exists only in the head contract.</summary>
    public bool IsAdded => Baseline is null;

    /// <summary>True when the operation exists only in the baseline contract.</summary>
    public bool IsRemoved => Head is null;
}

/// <summary>What a plugin rule found.</summary>
/// <param name="RuleId">The id of one of the plugin's <see cref="IRulePlugin.Rules"/>.</param>
/// <param name="At">
/// Where inside the operation. The location shapes are fixed, so suppressions, baselines and SARIF fingerprints treat
/// plugin findings like built-in ones. <see cref="SchemaPair.At"/> gives it for a schema node.
/// </param>
/// <param name="Message">A complete, standalone sentence.</param>
public sealed record PluginFinding(string RuleId, FindingLocation At, string Message);

/// <summary>What a rule may look at besides the operation pair. New members are added as properties only.</summary>
/// <param name="Baseline">The whole contract consumers rely on today.</param>
/// <param name="Head">The whole contract after the change.</param>
public sealed record RuleContext(ApiContract Baseline, ApiContract Head);
