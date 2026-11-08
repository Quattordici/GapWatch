namespace GapWatch.Core.Findings;

/// <summary>One detected difference between two contracts.</summary>
/// <param name="Rule">The rule that fired.</param>
/// <param name="Operation">Operation identity, e.g. <c>GET /pets/{id}</c>.</param>
/// <param name="Location">Where inside the operation, e.g. <c>response 200 application/json: $.name</c>.</param>
/// <param name="Message">Complete, standalone explanation.</param>
public sealed record Finding(RuleDescriptor Rule, string Operation, string Location, string Message)
{
    /// <summary>A finding whose <see cref="Location"/> is the string form of <paramref name="at"/>.</summary>
    public Finding(RuleDescriptor rule, string operation, FindingLocation at, string message)
        : this(rule, operation, (at ?? throw new ArgumentNullException(nameof(at))).ToString(), message)
    {
        At = at;
    }

    /// <summary>
    /// The structured form of <see cref="Location"/>, for code that needs the parameter, media type or status. Set by
    /// every built-in rule; null for a finding built from a location string alone.
    /// </summary>
    public FindingLocation? At { get; init; }

    /// <summary>
    /// Effective severity: the rule's default unless a <see cref="Policy.CompatibilityPolicy"/> overrode it.
    /// </summary>
    public Severity Severity { get; init; } = Rule.DefaultSeverity;

    /// <summary>
    /// The component schema the change originates in, when it is inside a <c>$ref</c>'d schema that both versions
    /// share. Findings with the same rule, origin and message describe one change seen through several operations.
    /// </summary>
    public SchemaOrigin? Origin { get; init; }

    /// <summary>
    /// Where the change is in the head document: the changed element, or the closest enclosing element that still
    /// exists (the schema a property was removed from, the operation a response was removed from). Null when the reader
    /// does not track positions or nothing of it exists in the head (a removed operation).
    /// </summary>
    public Model.SourcePosition? Position { get; init; }

    /// <summary>
    /// Where the change is in the code that implements the head contract (a controller action, an endpoint, a DTO
    /// property), when a source map was supplied and found it. Null otherwise.
    /// </summary>
    public CodeLocation? Code { get; init; }

    /// <summary>
    /// The API version the finding is about, for the version rules (GW060 to GW063). Findings with the same rule,
    /// version and message describe one decision seen through every operation of that version.
    /// </summary>
    public string? ApiVersion { get; init; }

    /// <summary>
    /// The head document <see cref="Position"/> refers to, when several head documents were compared (multi-document
    /// mode). Null means the single head document of the comparison.
    /// </summary>
    public string? Document { get; init; }

    /// <summary>What the members of a <see cref="FindingGroup"/> share: the component schema or the API version.</summary>
    public string? GroupLabel => Origin?.Display ?? (ApiVersion is null ? null : $"API version {ApiVersion}");
}

/// <summary>A place in a source file: the path as it was given to GapWatch, and a 1-based line and column.</summary>
public sealed record CodeLocation(string File, int Line, int Column)
{
    /// <summary><c>file:line:column</c>, the form compilers print and editors and build logs turn into links.</summary>
    public override string ToString() => $"{File}:{Line}:{Column}";
}

/// <summary>Where inside a shared component schema a change was found.</summary>
/// <param name="Component">Component schema name, e.g. <c>Pet</c> (from <c>#/components/schemas/Pet</c>).</param>
/// <param name="Path">Path inside that component, e.g. <c>$.name</c>; <c>$</c> is the component itself.</param>
/// <param name="Direction">Whether the component was compared as request or response content at this place.</param>
public sealed record SchemaOrigin(string Component, string Path, Direction Direction)
{
    /// <summary>Human-readable form, e.g. <c>Pet $.name (response)</c>.</summary>
    public string Display => $"{Component} {Path} ({Direction.ToString().ToLowerInvariant()})";
}

/// <summary>The outcome of comparing two contracts, after any policy was applied.</summary>
/// <param name="findings">Active findings: they count towards the summary and the exit code.</param>
/// <param name="suppressed">Findings silenced by a justified suppression. Reported for auditability, never counted.</param>
/// <param name="unusedSuppressions">Suppressions that matched no finding, usually because the change was reverted or fixed.</param>
public sealed class ComparisonResult(
    IReadOnlyList<Finding> findings,
    IReadOnlyList<SuppressedFinding>? suppressed = null,
    IReadOnlyList<Policy.Suppression>? unusedSuppressions = null)
{
    /// <summary>Active findings, most severe first, then by operation and location.</summary>
    public IReadOnlyList<Finding> Findings { get; } = findings;

    /// <summary>Findings silenced by a suppression, in the same order as <see cref="Findings"/>.</summary>
    public IReadOnlyList<SuppressedFinding> Suppressed { get; } = suppressed ?? [];

    /// <summary>Suppressions that matched nothing; candidates for removal from the configuration.</summary>
    public IReadOnlyList<Policy.Suppression> UnusedSuppressions { get; } = unusedSuppressions ?? [];

    /// <summary>Number of active findings with exactly the given severity.</summary>
    public int Count(Severity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>True when at least one active finding is at or above <paramref name="threshold"/>.</summary>
    public bool HasAtLeast(Severity threshold) => Findings.Any(f => f.Severity >= threshold);
}

/// <summary>A finding together with the suppression that silenced it.</summary>
public sealed record SuppressedFinding(Finding Finding, Policy.Suppression Suppression);

/// <summary>The canonical, deterministic order of findings in every report.</summary>
public static class FindingOrder
{
    /// <summary>Most severe first, then by operation, location, rule id and message (all ordinal).</summary>
    public static List<Finding> Sort(IEnumerable<Finding> findings) =>
        findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Operation, StringComparer.Ordinal)
            .ThenBy(f => f.Location, StringComparer.Ordinal)
            .ThenBy(f => f.Rule.Id, StringComparer.Ordinal)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// One change as a reader should see it: a single finding, or the same finding repeated across every operation that
/// uses a shared component schema or belongs to one API version.
/// </summary>
/// <param name="Members">The findings in this group, in report order. Never empty.</param>
public sealed record FindingGroup(IReadOnlyList<Finding> Members)
{
    /// <summary>The first member; rule, severity, message and origin are the same for every member.</summary>
    public Finding First => Members[0];

    /// <summary>True when the group stands for more than one finding.</summary>
    public bool IsShared => Members.Count > 1;

    /// <summary>
    /// Groups findings that share rule, effective severity, message and <see cref="Finding.Origin"/> or
    /// <see cref="Finding.ApiVersion"/>. Findings with neither stay on their own. Groups keep the position of their first member, so the result follows
    /// <see cref="FindingOrder"/>; counts and exit codes are unaffected because they are based on findings.
    /// </summary>
    public static IReadOnlyList<FindingGroup> Group(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var groups = new List<List<Finding>>();
        var byKey = new Dictionary<(string Rule, Severity Severity, string Message, SchemaOrigin? Origin, string? ApiVersion), List<Finding>>();

        foreach (var finding in findings)
        {
            if (finding.Origin is null && finding.ApiVersion is null)
            {
                groups.Add([finding]);
                continue;
            }

            var key = (finding.Rule.Id, finding.Severity, finding.Message, finding.Origin, finding.ApiVersion);
            if (!byKey.TryGetValue(key, out var members))
            {
                members = [];
                byKey[key] = members;
                groups.Add(members);
            }

            members.Add(finding);
        }

        return groups.Select(members => new FindingGroup(members)).ToList();
    }
}
