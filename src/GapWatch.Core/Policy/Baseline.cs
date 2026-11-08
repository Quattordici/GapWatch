using GapWatch.Core.Findings;

namespace GapWatch.Core.Policy;

/// <summary>One accepted finding in a <see cref="Baseline"/>.</summary>
/// <param name="RuleId">Rule id, e.g. <c>GW025</c>.</param>
/// <param name="Operation">Operation exactly as reported, e.g. <c>GET /pets</c>.</param>
/// <param name="Location">Location exactly as reported, e.g. <c>response 200 application/json: $[].name</c>.</param>
/// <param name="Message">The finding's message when the baseline was written. Read by people; only used to tell apart
/// findings that share rule, operation and location.</param>
public sealed record BaselineEntry(string RuleId, string Operation, string Location, string? Message);

/// <summary>
/// Findings that were accepted all at once, typically when GapWatch is adopted against a base contract that is already
/// far behind (a published release). Unlike suppressions, a baseline is generated, matches exactly and never uses
/// wildcards, so it cannot hide a new change; one justification covers the whole file.
/// </summary>
/// <param name="Justification">Why these findings are accepted; shown next to every baselined finding.</param>
/// <param name="Entries">The accepted findings.</param>
public sealed record Baseline(string Justification, IReadOnlyList<BaselineEntry> Entries)
{
    /// <summary>The baseline that accepts nothing.</summary>
    public static Baseline Empty { get; } = new(string.Empty, []);

    /// <summary>A baseline that accepts exactly the given findings.</summary>
    public static Baseline From(IEnumerable<Finding> findings, string justification)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        var entries = FindingOrder.Sort(findings)
            .Select(f => new BaselineEntry(f.Rule.Id, f.Operation, f.Location, f.Message))
            .ToList();
        return new Baseline(justification, entries);
    }

    /// <summary>
    /// Moves every active finding that an entry accepts to <see cref="ComparisonResult.Suppressed"/>. Each entry accepts
    /// at most one finding: the one with the same rule, operation and location, preferring the same message. Entries
    /// that accept nothing are reported as unused, because the change was fixed, reverted, or has shipped.
    /// </summary>
    public ComparisonResult Apply(ComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (Entries.Count == 0)
        {
            return result;
        }

        var open = Entries
            .GroupBy(e => (e.RuleId, e.Operation, e.Location))
            .ToDictionary(g => g.Key, g => g.ToList());

        var active = new List<Finding>();
        var suppressed = new List<SuppressedFinding>();

        // Exact message matches first, so a reworded duplicate cannot take another finding's entry.
        var matched = new Dictionary<Finding, BaselineEntry>(ReferenceEqualityComparer.Instance);
        foreach (var pass in new[] { true, false })
        {
            foreach (var finding in result.Findings)
            {
                if (matched.ContainsKey(finding)
                    || !open.TryGetValue((finding.Rule.Id, finding.Operation, finding.Location), out var candidates))
                {
                    continue;
                }

                var entry = pass
                    ? candidates.FirstOrDefault(e => string.Equals(e.Message, finding.Message, StringComparison.Ordinal))
                    : candidates.FirstOrDefault();
                if (entry is not null)
                {
                    candidates.Remove(entry);
                    matched[finding] = entry;
                }
            }
        }

        foreach (var finding in result.Findings)
        {
            if (matched.TryGetValue(finding, out var entry))
            {
                suppressed.Add(new SuppressedFinding(finding, ToSuppression(entry)));
            }
            else
            {
                active.Add(finding);
            }
        }

        suppressed.AddRange(result.Suppressed);
        var unused = result.UnusedSuppressions
            .Concat(open.Values.SelectMany(e => e).Select(ToSuppression))
            .ToList();

        return new ComparisonResult(active, suppressed, unused);
    }

    private Suppression ToSuppression(BaselineEntry entry) =>
        new(entry.RuleId, entry.Operation, entry.Location, Justification) { IsBaseline = true };
}
