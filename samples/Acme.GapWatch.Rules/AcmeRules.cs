using GapWatch.Core.Findings;
using GapWatch.Sdk;

namespace Acme.GapWatch.Rules;

/// <summary>
/// Two house rules for the Acme APIs, as a pattern to copy. Load the built assembly with
/// <c>--plugin Acme.GapWatch.Rules.dll</c> or <c>"plugins": [{ "path": "..." }]</c> in <c>gapwatch.json</c>.
/// </summary>
public sealed class AcmeRules : IRulePlugin
{
    /// <summary>
    /// ACME001: an added operation that allows anonymous access. Not schema-directional: it looks at added operations
    /// only, so existing open endpoints are not reported and adopting the rule needs no baseline.
    /// </summary>
    public static readonly RuleDescriptor NewOperationWithoutSecurity = new(
        "ACME001",
        nameof(NewOperationWithoutSecurity),
        Severity.Breaking,
        "A new operation allows anonymous access. Acme APIs require authentication on every new endpoint.")
    {
        HelpUri = new Uri("https://github.com/Quattordici/GapWatch/blob/main/docs/plugins.md#the-sample-plugin"),
    };

    /// <summary>
    /// ACME002: a new string that clients send, with no <c>maxLength</c>. Request direction only: an unbounded string
    /// clients send is an input-validation gap, while an unbounded string in a response limits nobody.
    /// </summary>
    public static readonly RuleDescriptor UnboundedRequestString = new(
        "ACME002",
        nameof(UnboundedRequestString),
        Severity.Warning,
        "A new string in a request has no maxLength. Acme APIs bound every string a client sends.")
    {
        HelpUri = new Uri("https://github.com/Quattordici/GapWatch/blob/main/docs/plugins.md#the-sample-plugin"),
    };

    /// <inheritdoc />
    public string Prefix => "ACME";

    /// <inheritdoc />
    public IReadOnlyList<RuleDescriptor> Rules { get; } = [NewOperationWithoutSecurity, UnboundedRequestString];

    /// <inheritdoc />
    public IEnumerable<PluginFinding> Check(OperationPair pair, RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(pair);

        if (pair is { Baseline: null, Head: { } head } && IsOpen(head.Security))
        {
            yield return new PluginFinding(
                NewOperationWithoutSecurity.Id,
                FindingLocation.Operation.Instance,
                $"New operation '{pair.Operation}' allows anonymous access.");
        }

        foreach (var node in SchemaWalker.Walk(pair))
        {
            // Only what is new in the head: a string that was unbounded before was reported when it was added.
            if (node is { Direction: Direction.Request, After: { MaxLength: null } after }
                && after.Types.Contains("string")
                && after.Format is null or "" && after.EnumValues.Count == 0
                && (node.Before is null || !node.Before.Types.Contains("string")))
            {
                yield return new PluginFinding(UnboundedRequestString.Id, node.At, "This new request string has no maxLength.");
            }
        }
    }

    /// <summary>No requirement, or the alternative <c>none</c>, means anonymous callers are accepted.</summary>
    private static bool IsOpen(IReadOnlyList<string> security) => security.Count == 0 || security.Contains("none");
}
