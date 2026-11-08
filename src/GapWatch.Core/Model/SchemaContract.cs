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

    /// <summary><c>minLength</c> for strings.</summary>
    public Bound? MinLength { get; init; }

    /// <summary><c>maxLength</c> for strings.</summary>
    public Bound? MaxLength { get; init; }

    /// <summary><c>minItems</c> for arrays.</summary>
    public Bound? MinItems { get; init; }

    /// <summary><c>maxItems</c> for arrays.</summary>
    public Bound? MaxItems { get; init; }

    /// <summary>
    /// Lower bound for numbers: <c>minimum</c>, or <c>exclusiveMinimum</c> in either the OpenAPI 3.0 form (a flag on
    /// <c>minimum</c>) or the 3.1 form (a number of its own).
    /// </summary>
    public Bound? Minimum { get; init; }

    /// <summary>Upper bound for numbers, normalized like <see cref="Minimum"/>.</summary>
    public Bound? Maximum { get; init; }

    /// <summary>The <c>pattern</c> regular expression, exactly as written.</summary>
    public string? Pattern { get; init; }

    /// <summary>False when <c>additionalProperties: false</c>; absent, <c>true</c> or a schema all allow extra properties.</summary>
    public bool AdditionalPropertiesAllowed { get; init; } = true;

    /// <summary>The <c>discriminator</c> of a polymorphic schema, if any.</summary>
    public Discriminator? Discriminator { get; init; }

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

/// <summary>A lower or upper bound of a length, an item count or a number.</summary>
/// <param name="Value">The limit.</param>
/// <param name="Exclusive">True when the limit itself is not allowed (only for numbers).</param>
public sealed record Bound(double Value, bool Exclusive = false)
{
    /// <summary>Whether this lower bound allows fewer values than <paramref name="other"/> (it is tighter).</summary>
    public bool IsTighterMinimumThan(Bound other) => Value > other.Value || (Value == other.Value && Exclusive && !other.Exclusive);

    /// <summary>Whether this upper bound allows fewer values than <paramref name="other"/> (it is tighter).</summary>
    public bool IsTighterMaximumThan(Bound other) => Value < other.Value || (Value == other.Value && Exclusive && !other.Exclusive);

    /// <summary><c>5</c>, or <c>5 (exclusive)</c>.</summary>
    public override string ToString() =>
        Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + (Exclusive ? " (exclusive)" : string.Empty);
}

/// <summary>The <c>discriminator</c> object: which property selects the subtype, and which value means which schema.</summary>
/// <param name="PropertyName">The property that carries the subtype name.</param>
/// <param name="Mapping">Property value to component schema name (from <c>#/components/schemas/Name</c>), sorted by value.</param>
public sealed record Discriminator(string PropertyName, IReadOnlyDictionary<string, string> Mapping);
