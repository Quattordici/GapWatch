namespace GapWatch.Core.Findings;

/// <summary>How serious a finding is. Ordered so that comparisons (<c>&gt;=</c>) work.</summary>
public enum Severity
{
    /// <summary>A safe or purely informational change.</summary>
    Info = 0,

    /// <summary>A change that can break some consumers; review it.</summary>
    Warning = 1,

    /// <summary>A change that breaks existing, correct consumers.</summary>
    Breaking = 2,
}

/// <summary>Whether a schema is being compared as something consumers send or something they receive.</summary>
public enum Direction
{
    /// <summary>Consumers produce it (request bodies, parameters): the server must keep accepting what it accepted.</summary>
    Request,

    /// <summary>Consumers read it (responses): the server must keep producing what consumers expect.</summary>
    Response,
}

/// <summary>Static description of a rule; the unit users will configure by id.</summary>
/// <param name="Id">Stable identifier such as <c>GW025</c>. Ids are never reused.</param>
/// <param name="Name">Short PascalCase name.</param>
/// <param name="DefaultSeverity">Severity unless overridden.</param>
/// <param name="Description">What the rule detects and why it matters.</param>
public sealed record RuleDescriptor(string Id, string Name, Severity DefaultSeverity, string Description);

/// <summary>One detected difference between two contracts.</summary>
/// <param name="Rule">The rule that fired.</param>
/// <param name="Operation">Operation identity, e.g. <c>GET /pets/{id}</c>.</param>
/// <param name="Location">Where inside the operation, e.g. <c>response 200 application/json: $.name</c>.</param>
/// <param name="Message">Complete, standalone explanation.</param>
public sealed record Finding(RuleDescriptor Rule, string Operation, string Location, string Message)
{
    /// <summary>Severity of the rule that produced this finding.</summary>
    public Severity Severity => Rule.DefaultSeverity;
}

/// <summary>The outcome of comparing two contracts.</summary>
public sealed class ComparisonResult(IReadOnlyList<Finding> findings)
{
    /// <summary>All findings, most severe first, then by operation and location.</summary>
    public IReadOnlyList<Finding> Findings { get; } = findings;

    /// <summary>Number of findings with exactly the given severity.</summary>
    public int Count(Severity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>True when at least one finding is at or above <paramref name="threshold"/>.</summary>
    public bool HasAtLeast(Severity threshold) => Findings.Any(f => f.Severity >= threshold);
}
