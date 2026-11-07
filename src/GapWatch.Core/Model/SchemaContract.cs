using System.Collections.Frozen;

namespace GapWatch.Core.Model;

/// <summary>
/// A resolved, reference-free schema node. <c>$ref</c>s and <c>allOf</c> compositions have already been
/// flattened by the reader. Reference cycles are cut with <see cref="IsRecursiveReference"/> leaves.
/// </summary>
public sealed record SchemaContract
{
    /// <summary>A schema that constrains nothing (no type, no properties).</summary>
    public static SchemaContract Unspecified { get; } = new();

    /// <summary>Accepted JSON types, sorted, excluding <c>null</c>. Empty means "unspecified / any".</summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>The <c>format</c> keyword, if any (<c>int32</c>, <c>date-time</c>, ...).</summary>
    public string? Format { get; init; }

    /// <summary>Whether <c>null</c> is an accepted / possible value.</summary>
    public bool Nullable { get; init; }

    /// <summary>Allowed values as canonical strings, sorted. Empty means unconstrained.</summary>
    public IReadOnlyList<string> EnumValues { get; init; } = [];

    /// <summary>Object properties by name.</summary>
    public IReadOnlyDictionary<string, PropertyContract> Properties { get; init; } =
        FrozenDictionary<string, PropertyContract>.Empty;

    /// <summary>Element schema for arrays.</summary>
    public SchemaContract? Items { get; init; }

    /// <summary><c>oneOf</c> / <c>anyOf</c> alternatives (a bare <c>null</c> alternative becomes <see cref="Nullable"/>).</summary>
    public IReadOnlyList<SchemaContract> Variants { get; init; } = [];

    /// <summary>Name of the component schema this node came from, when it came from a <c>$ref</c>.</summary>
    public string? ReferenceName { get; init; }

    /// <summary>True when this node stands in for a schema that is already being expanded higher up (a cycle).</summary>
    public bool IsRecursiveReference { get; init; }

    /// <summary>
    /// Where the schema is defined in the source document: for a <c>$ref</c>, the referenced definition. Not part of
    /// the compared contract.
    /// </summary>
    public SourcePosition? Position { get; init; }
}

/// <summary>An object property together with the flags that matter for compatibility.</summary>
/// <param name="Schema">Value schema.</param>
/// <param name="Required">Listed in the parent's <c>required</c> array.</param>
/// <param name="ReadOnly">Only ever sent by the server (never part of requests).</param>
/// <param name="WriteOnly">Only ever sent by the client (never part of responses).</param>
public sealed record PropertyContract(SchemaContract Schema, bool Required, bool ReadOnly, bool WriteOnly)
{
    /// <summary>Where the property name is declared, which differs from the schema's position when it is a <c>$ref</c>.</summary>
    public SourcePosition? Position { get; init; }
}
