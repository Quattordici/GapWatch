using GapWatch.Core.Findings;

namespace GapWatch.Core.Policy;

/// <summary>
/// Silences findings of one rule, optionally narrowed to an operation and a location. A suppression must say why:
/// the justification is shown in every report so reviewers can audit what was accepted.
/// </summary>
/// <param name="RuleId">Rule id such as <c>GW025</c>. Required.</param>
/// <param name="Operation">
/// Operation pattern such as <c>GET /pets/{id}</c>, or <c>null</c> for every operation. <c>*</c> matches any run of
/// characters; everything else matches literally and case-sensitively.
/// </param>
/// <param name="Location">Location pattern (same syntax as <paramref name="Operation"/>), or <c>null</c> for every location.</param>
/// <param name="Justification">Why the change is acceptable. Required and must not be blank.</param>
public sealed record Suppression(string RuleId, string? Operation, string? Location, string Justification)
{
    /// <summary>True when this suppression covers <paramref name="finding"/>.</summary>
    public bool Matches(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return string.Equals(RuleId, finding.Rule.Id, StringComparison.Ordinal)
               && (Operation is null || Wildcard.IsMatch(finding.Operation, Operation))
               && (Location is null || Wildcard.IsMatch(finding.Location, Location));
    }

    /// <summary>Compact human-readable form, e.g. <c>GW025 GET /pets response 200: $.name</c>.</summary>
    public string Describe() =>
        string.Join(' ', new[] { RuleId, Operation ?? "*", Location }.Where(part => part is not null));
}

/// <summary>
/// Project-specific adjustments to the rule set: severity overrides, disabled rules and justified suppressions.
/// Applied after comparison, so rules never need to know about configuration.
/// </summary>
/// <param name="SeverityOverrides">Rule id to the severity it should report with instead of its default.</param>
/// <param name="DisabledRules">Rule ids whose findings are dropped entirely (not even listed as suppressed).</param>
/// <param name="Suppressions">Justified exceptions for individual findings.</param>
public sealed record CompatibilityPolicy(
    IReadOnlyDictionary<string, Severity> SeverityOverrides,
    IReadOnlySet<string> DisabledRules,
    IReadOnlyList<Suppression> Suppressions)
{
    /// <summary>The policy that changes nothing.</summary>
    public static CompatibilityPolicy Default { get; } = new(
        new Dictionary<string, Severity>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        []);

    /// <summary>
    /// Returns every problem with this policy (unknown rule ids, blank justifications, contradictory settings).
    /// An empty list means the policy is valid. Messages are complete sentences.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var known = RuleCatalog.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var id in SeverityOverrides.Keys.Concat(DisabledRules).Order(StringComparer.Ordinal))
        {
            if (!known.Contains(id))
            {
                problems.Add($"Unknown rule id '{id}'. Run 'gapwatch rules' to list the valid ids.");
            }
        }

        foreach (var id in SeverityOverrides.Keys.Intersect(DisabledRules).Order(StringComparer.Ordinal))
        {
            problems.Add($"Rule '{id}' is both disabled and given a severity.");
        }

        for (var i = 0; i < Suppressions.Count; i++)
        {
            var suppression = Suppressions[i];
            var label = $"Suppression #{i + 1} ({suppression.Describe()})";

            if (!known.Contains(suppression.RuleId))
            {
                problems.Add($"{label} refers to unknown rule id '{suppression.RuleId}'.");
            }

            if (string.IsNullOrWhiteSpace(suppression.Justification))
            {
                problems.Add($"{label} has no justification. Every suppression must say why the change is acceptable.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Applies the policy to a raw comparison: drops disabled rules, applies severity overrides, then moves findings
    /// matched by a suppression to <see cref="ComparisonResult.Suppressed"/>. The first matching suppression wins.
    /// </summary>
    /// <exception cref="ArgumentException">The policy is invalid (see <see cref="Validate"/>).</exception>
    public ComparisonResult Apply(ComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var problems = Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(result));
        }

        var adjusted = result.Findings
            .Where(f => !DisabledRules.Contains(f.Rule.Id))
            .Select(f => SeverityOverrides.TryGetValue(f.Rule.Id, out var severity) ? f with { Severity = severity } : f);

        var active = new List<Finding>();
        var suppressed = new List<SuppressedFinding>();
        var used = new HashSet<Suppression>(ReferenceEqualityComparer.Instance);

        foreach (var finding in FindingOrder.Sort(adjusted))
        {
            var match = Suppressions.FirstOrDefault(s => s.Matches(finding));
            if (match is null)
            {
                active.Add(finding);
                continue;
            }

            suppressed.Add(new SuppressedFinding(finding, match));
            used.Add(match);
        }

        // Findings an earlier policy already suppressed stay suppressed.
        suppressed.AddRange(result.Suppressed);

        var unused = Suppressions.Where(s => !used.Contains(s)).ToList();
        return new ComparisonResult(active, suppressed, unused);
    }
}

/// <summary>Minimal glob matching where <c>*</c> matches any run of characters (including none).</summary>
internal static class Wildcard
{
    public static bool IsMatch(string text, string pattern)
    {
        var t = 0;
        var p = 0;
        var star = -1;
        var resume = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = t;
            }
            else if (p < pattern.Length && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
