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
/// <param name="Id">
/// Stable identifier such as <c>GW025</c>, or <c>ACME001</c> for a plugin rule (its prefix, then digits). Ids are never
/// reused.
/// </param>
/// <param name="Name">Short PascalCase name.</param>
/// <param name="DefaultSeverity">Severity unless overridden.</param>
/// <param name="Description">What the rule detects and why it matters.</param>
public sealed record RuleDescriptor(string Id, string Name, Severity DefaultSeverity, string Description)
{
    /// <summary>
    /// Where the rule is documented, for a plugin rule. SARIF reports it as the rule's <c>helpUri</c>. Built-in rules
    /// leave it null: they are documented in GapWatch's rule list.
    /// </summary>
    public Uri? HelpUri { get; init; }
}
