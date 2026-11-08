using GapWatch.Core.Findings;
using GapWatch.Core.Model;

namespace GapWatch.Core.Comparison;

/// <summary>
/// Receives findings from the schema comparer: the schema path such as <c>$.items[].name</c>, and the component schema
/// the change originates in (the innermost <c>$ref</c> that both versions share), if any, and where the change is in the
/// head document, if known.
/// </summary>
internal delegate void SchemaFindingSink(
    RuleDescriptor rule, string schemaPath, string message, SourcePosition? position, SchemaOrigin? origin);

/// <summary>
/// Emits a finding at a schema path; the origin is filled in by the enclosing comparison. A null
/// <paramref name="position"/> means the head schema node being compared (or its closest positioned ancestor).
/// </summary>
internal delegate void ReportFinding(RuleDescriptor rule, string schemaPath, string message, SourcePosition? position = null);

/// <summary>
/// Direction-aware structural comparison of two schemas. The same structural change is breaking or safe depending on
/// whether consumers send the value (<see cref="Direction.Request"/>) or read it (<see cref="Direction.Response"/>).
/// </summary>
internal static class SchemaComparer
{
    public const string Root = FindingLocation.Root;

    private static readonly Dictionary<string, (string Family, int Rank)> NumericFormats = new(StringComparer.Ordinal)
    {
        ["int32"] = ("int", 1),
        ["int64"] = ("int", 2),
        ["float"] = ("fp", 1),
        ["double"] = ("fp", 2),
    };

    public static void Compare(
        SchemaContract baseline,
        SchemaContract head,
        Direction direction,
        string path,
        SourcePosition? position,
        SchemaFindingSink sink) =>
        Compare(baseline, head, direction, path, position, sink, scope: null);

    /// <summary>The component schema currently being compared and where its root sits in the full path.</summary>
    private readonly record struct ComponentScope(string Name, int PathStart);

    private static void Compare(
        SchemaContract baseline,
        SchemaContract head,
        Direction direction,
        string path,
        SourcePosition? fallback,
        SchemaFindingSink sink,
        ComponentScope? scope)
    {
        // A recursive leaf only names a schema that is expanded (and therefore compared) higher up.
        if (baseline.IsRecursiveReference || head.IsRecursiveReference)
        {
            return;
        }

        // Entering a component schema that both versions reference: changes below originate in that component.
        // Changes on the referencing node itself (nullable or other keywords next to the $ref) belong to the place that
        // references it, so they stay with the enclosing component when there is one.
        var enclosing = scope;
        if (baseline.ReferenceName is { } name && string.Equals(name, head.ReferenceName, StringComparison.Ordinal))
        {
            scope = new ComponentScope(name, path.Length);
        }

        var here = head.Position ?? fallback;
        ReportFinding report = (rule, schemaPath, message, position) =>
        {
            var owner = schemaPath == path && enclosing is not null ? enclosing : scope;
            sink(
                rule,
                schemaPath,
                message,
                position ?? here,
                owner is { } s ? new SchemaOrigin(s.Name, Root + schemaPath[s.PathStart..], direction) : null);
        };

        if (!CompareTypes(baseline, head, direction, path, report))
        {
            return;
        }

        CompareFormat(baseline, head, direction, path, report);
        CompareNullability(baseline, head, direction, path, report);
        CompareEnums(baseline, head, direction, path, report);
        CompareBounds(baseline, head, direction, path, report);
        ComparePattern(baseline, head, direction, path, report);
        CompareAdditionalProperties(baseline, head, direction, path, report);
        CompareDiscriminator(baseline, head, direction, path, report);
        CompareProperties(baseline, head, direction, path, here, report, sink, scope);

        if (baseline.Items is not null && head.Items is not null)
        {
            Compare(baseline.Items, head.Items, direction, $"{path}[]", here, sink, scope);
        }

        CompareVariants(baseline, head, direction, path, here, report, sink, scope);
    }

    private static bool CompareTypes(
        SchemaContract baseline, SchemaContract head, Direction direction, string path, ReportFinding report)
    {
        if (baseline.Types.SequenceEqual(head.Types, StringComparer.Ordinal))
        {
            return true;
        }

        // Request: the server must still accept everything it accepted before (head is a superset).
        // Response: the server must not start returning anything new (head is a subset).
        var compatible = direction == Direction.Request
            ? IsSubset(baseline.Types, head.Types)
            : IsSubset(head.Types, baseline.Types);

        if (compatible)
        {
            return true;
        }

        report(
            RuleCatalog.TypeChanged,
            path,
            $"Type changed from {DescribeTypes(baseline.Types)} to {DescribeTypes(head.Types)}.");
        return false;
    }

    /// <summary>True when every type in <paramref name="narrow"/> is accepted by <paramref name="wide"/>. Empty means "any".</summary>
    private static bool IsSubset(IReadOnlyList<string> narrow, IReadOnlyList<string> wide)
    {
        if (wide.Count == 0)
        {
            return true;
        }

        if (narrow.Count == 0)
        {
            return false;
        }

        return narrow.All(n => wide.Contains(n, StringComparer.Ordinal)
                               || (n == "integer" && wide.Contains("number", StringComparer.Ordinal)));
    }

    private static void CompareFormat(
        SchemaContract baseline, SchemaContract head, Direction direction, string path, ReportFinding report)
    {
        if (string.Equals(baseline.Format, head.Format, StringComparison.Ordinal))
        {
            return;
        }

        bool breaking;
        if (baseline.Format is null)
        {
            // A free value became format-constrained: narrowing, so only requests can break.
            breaking = direction == Direction.Request;
        }
        else if (head.Format is null)
        {
            // A formatted value became free-form: consumers relying on the format can break on responses.
            breaking = direction == Direction.Response;
        }
        else if (NumericFormats.TryGetValue(baseline.Format, out var b)
                 && NumericFormats.TryGetValue(head.Format, out var h)
                 && b.Family == h.Family)
        {
            // int32 -> int64 widens: safe to accept, unsafe to return.
            breaking = direction == Direction.Request ? h.Rank < b.Rank : h.Rank > b.Rank;
        }
        else
        {
            breaking = true;
        }

        if (breaking)
        {
            report(
                RuleCatalog.FormatChanged,
                path,
                $"Format changed from {Quote(baseline.Format)} to {Quote(head.Format)}.");
        }
    }

    private static void CompareNullability(
        SchemaContract baseline, SchemaContract head, Direction direction, string path, ReportFinding report)
    {
        if (direction == Direction.Request && baseline.Nullable && !head.Nullable)
        {
            report(RuleCatalog.NullabilityChanged, path, "The value no longer accepts null.");
        }
        else if (direction == Direction.Response && !baseline.Nullable && head.Nullable)
        {
            report(RuleCatalog.NullabilityChanged, path, "The value can now be null.");
        }
    }

    private static void CompareEnums(
        SchemaContract baseline, SchemaContract head, Direction direction, string path, ReportFinding report)
    {
        var before = baseline.EnumValues;
        var after = head.EnumValues;

        if (direction == Direction.Request)
        {
            if (after.Count == 0)
            {
                return;
            }

            if (before.Count == 0)
            {
                report(
                    RuleCatalog.EnumValueRemoved,
                    path,
                    $"An enum constraint ({List(after)}) was added to a previously unconstrained value.");
                return;
            }

            var removed = before.Except(after, StringComparer.Ordinal).ToList();
            if (removed.Count > 0)
            {
                report(RuleCatalog.EnumValueRemoved, path, $"Enum value(s) no longer accepted: {List(removed)}.");
            }

            return;
        }

        if (before.Count == 0)
        {
            return;
        }

        if (after.Count == 0)
        {
            report(RuleCatalog.EnumValueAdded, path, "The enum constraint was removed; any value can now be returned.");
            return;
        }

        var added = after.Except(before, StringComparer.Ordinal).ToList();
        if (added.Count > 0)
        {
            report(RuleCatalog.EnumValueAdded, path, $"New enum value(s) can be returned: {List(added)}.");
        }
    }

    /// <summary>
    /// GW032 for a tighter bound in a request, GW033 for a looser one in a response. The opposite direction is safe:
    /// a server may accept more than before, and return less.
    /// </summary>
    private static void CompareBounds(
        SchemaContract baseline, SchemaContract head, Direction direction, string path, ReportFinding report)
    {
        CompareBound("minLength", baseline.MinLength, head.MinLength, isMinimum: true, direction, path, report);
        CompareBound("maxLength", baseline.MaxLength, head.MaxLength, isMinimum: false, direction, path, report);
        CompareBound("minItems", baseline.MinItems, head.MinItems, isMinimum: true, direction, path, report);
        CompareBound("maxItems", baseline.MaxItems, head.MaxItems, isMinimum: false, direction, path, report);
        CompareBound("minimum", baseline.Minimum, head.Minimum, isMinimum: true, direction, path, report);
        CompareBound("maximum", baseline.Maximum, head.Maximum, isMinimum: false, direction, path, report);
    }

    private static void CompareBound(
        string keyword, Bound? before, Bound? after, bool isMinimum, Direction direction, string path, ReportFinding report)
    {
        if (before == after)
        {
            return;
        }

        // Adding a bound narrows, removing one widens; otherwise compare the limits.
        var narrowed = before is null
            || (after is not null && (isMinimum ? after.IsTighterMinimumThan(before) : after.IsTighterMaximumThan(before)));
        var widened = after is null
            || (before is not null && (isMinimum ? before.IsTighterMinimumThan(after) : before.IsTighterMaximumThan(after)));

        if (direction == Direction.Request && narrowed)
        {
            report(
                RuleCatalog.ValueRangeNarrowed,
                path,
                before is null
                    ? $"A {keyword} of {after} was added; values outside it are no longer accepted."
                    : $"{Capitalize(keyword)} changed from {before} to {after}; values between them are no longer accepted.");
        }
        else if (direction == Direction.Response && widened)
        {
            report(
                RuleCatalog.ValueRangeWidened,
                path,
                after is null
                    ? $"The {keyword} of {before} was removed; any value can now be returned."
                    : $"{Capitalize(keyword)} changed from {before} to {after}; values between them can now be returned.");
        }
    }

    private static void ComparePattern(
        SchemaContract baseline, SchemaContract head, Direction direction, string path, ReportFinding report)
    {
        if (string.Equals(baseline.Pattern, head.Pattern, StringComparison.Ordinal))
        {
            return;
        }

        if (baseline.Pattern is null)
        {
            if (direction == Direction.Request)
            {
                report(RuleCatalog.PatternAdded, path, $"A pattern '{head.Pattern}' was added; values that do not match it are no longer accepted.");
            }

            return;
        }

        if (head.Pattern is null)
        {
            if (direction == Direction.Response)
            {
                report(RuleCatalog.PatternChanged, path, $"The pattern '{baseline.Pattern}' was removed; any string can now be returned.");
            }

            return;
        }

        report(
            RuleCatalog.PatternChanged,
            path,
            direction == Direction.Request
                ? $"Pattern changed from '{baseline.Pattern}' to '{head.Pattern}'; check that every value the old one accepted still matches."
                : $"Pattern changed from '{baseline.Pattern}' to '{head.Pattern}'; check that consumers accept every value the new one allows.");
    }

    private static void CompareAdditionalProperties(
        SchemaContract baseline, SchemaContract head, Direction direction, string path, ReportFinding report)
    {
        if (direction == Direction.Request && baseline.AdditionalPropertiesAllowed && !head.AdditionalPropertiesAllowed)
        {
            report(
                RuleCatalog.AdditionalPropertiesRestricted,
                path,
                "additionalProperties became false; requests with properties that are not declared are rejected where the contract is enforced.");
        }
    }

    /// <summary>
    /// A renamed discriminator property or a value that selects another schema breaks both directions, because clients
    /// pick the subtype by it either way. A removed value only breaks requests; a value added to a response is GW031.
    /// </summary>
    private static void CompareDiscriminator(
        SchemaContract baseline, SchemaContract head, Direction direction, string path, ReportFinding report)
    {
        if (baseline.Discriminator is not { } before || head.Discriminator is not { } after)
        {
            return;
        }

        if (!string.Equals(before.PropertyName, after.PropertyName, StringComparison.Ordinal))
        {
            report(
                RuleCatalog.DiscriminatorChanged,
                path,
                $"The discriminator property changed from '{before.PropertyName}' to '{after.PropertyName}'.");
            return;
        }

        foreach (var (value, schema) in before.Mapping.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!after.Mapping.TryGetValue(value, out var now))
            {
                if (direction == Direction.Request)
                {
                    report(
                        RuleCatalog.DiscriminatorChanged,
                        path,
                        $"Discriminator value '{value}' ({schema}) is no longer accepted.");
                }
            }
            else if (!string.Equals(schema, now, StringComparison.Ordinal))
            {
                report(
                    RuleCatalog.DiscriminatorChanged,
                    path,
                    $"Discriminator value '{value}' now selects '{now}' instead of '{schema}'.");
            }
        }
    }

    private static string Capitalize(string keyword) => char.ToUpperInvariant(keyword[0]) + keyword[1..];

    private static void CompareProperties(
        SchemaContract baseline,
        SchemaContract head,
        Direction direction,
        string path,
        SourcePosition? here,
        ReportFinding report,
        SchemaFindingSink sink,
        ComponentScope? scope)
    {
        var before = Visible(baseline, direction);
        var after = Visible(head, direction);

        foreach (var (name, was) in before.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var propertyPath = $"{path}.{name}";

            if (!after.TryGetValue(name, out var now))
            {
                if (direction == Direction.Response && was.Required)
                {
                    report(
                        RuleCatalog.RequiredPropertyRemoved,
                        propertyPath,
                        $"Required response property '{name}' was removed.");
                }
                else if (direction == Direction.Response)
                {
                    report(
                        RuleCatalog.PropertyRemoved,
                        propertyPath,
                        $"Optional response property '{name}' was removed.");
                }
                else
                {
                    report(
                        RuleCatalog.PropertyRemoved,
                        propertyPath,
                        $"Request property '{name}' was removed; callers that still send it may be rejected or ignored.");
                }

                continue;
            }

            if (direction == Direction.Request && !was.Required && now.Required)
            {
                report(
                    RuleCatalog.PropertyBecameRequired,
                    propertyPath,
                    $"Request property '{name}' became required.",
                    now.Position);
            }
            else if (direction == Direction.Response && was.Required && !now.Required)
            {
                report(
                    RuleCatalog.PropertyBecameOptional,
                    propertyPath,
                    $"Response property '{name}' is no longer guaranteed to be present.",
                    now.Position);
            }

            Compare(was.Schema, now.Schema, direction, propertyPath, now.Position ?? here, sink, scope);
        }

        if (direction != Direction.Request)
        {
            return;
        }

        foreach (var (name, now) in after.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (now.Required && !before.ContainsKey(name))
            {
                report(
                    RuleCatalog.RequiredPropertyAdded,
                    $"{path}.{name}",
                    $"New required request property '{name}' was added.",
                    now.Position);
            }
        }
    }

    private static void CompareVariants(
        SchemaContract baseline,
        SchemaContract head,
        Direction direction,
        string path,
        SourcePosition? here,
        ReportFinding report,
        SchemaFindingSink sink,
        ComponentScope? scope)
    {
        if (baseline.Variants.Count == 0 && head.Variants.Count == 0)
        {
            return;
        }

        var before = ByKey(baseline.Variants);
        var after = ByKey(head.Variants);

        foreach (var (key, was) in before.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (after.TryGetValue(key, out var now))
            {
                Compare(was, now, direction, $"{path}<{key}>", here, sink, scope);
            }
            else if (direction == Direction.Request)
            {
                report(RuleCatalog.VariantRemoved, path, $"Alternative '{key}' is no longer accepted.");
            }
        }

        if (direction != Direction.Response)
        {
            return;
        }

        foreach (var key in after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            report(RuleCatalog.VariantAdded, path, $"New alternative '{key}' can be returned.", after[key].Position);
        }
    }

    private static Dictionary<string, PropertyContract> Visible(SchemaContract schema, Direction direction) =>
        schema.Properties
            .Where(p => direction == Direction.Request ? !p.Value.ReadOnly : !p.Value.WriteOnly)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static Dictionary<string, SchemaContract> ByKey(IReadOnlyList<SchemaContract> variants)
    {
        var map = new Dictionary<string, SchemaContract>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var key = variant.ReferenceName ?? Signature(variant);
            while (!map.TryAdd(key, variant))
            {
                key += "'";
            }
        }

        return map;
    }

    private static string Signature(SchemaContract schema)
    {
        var types = schema.Types.Count == 0 ? "any" : string.Join("|", schema.Types);
        return schema.Properties.Count == 0
            ? types
            : $"{types}{{{string.Join(",", schema.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal))}}}";
    }

    private static string DescribeTypes(IReadOnlyList<string> types) =>
        types.Count == 0 ? "'any'" : $"'{string.Join("|", types)}'";

    private static string Quote(string? value) => value is null ? "'none'" : $"'{value}'";

    private static string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}
