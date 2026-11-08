using GapWatch.Core.Findings;
using GapWatch.Core.Model;

namespace GapWatch.Sdk;

/// <summary>
/// How the two sides of a comparison are matched up: operations, parameters, responses, properties and variants. The
/// comparer in Core and <see cref="SchemaWalker"/> use the same keys, so a plugin sees exactly the pairs the built-in
/// rules see.
/// </summary>
internal static class Pairing
{
    /// <summary>Operations by <see cref="OperationContract.Key"/>; the first one wins when two share a key.</summary>
    public static Dictionary<string, OperationContract> Operations(ApiContract contract)
    {
        var map = new Dictionary<string, OperationContract>(StringComparer.Ordinal);
        foreach (var operation in contract.Operations)
        {
            map.TryAdd(operation.Key, operation);
        }

        return map;
    }

    /// <summary>
    /// Every operation pair: those of the baseline in its order (matched or removed), then the added ones in head order.
    /// </summary>
    public static IEnumerable<OperationPair> OperationPairs(ApiContract baseline, ApiContract head)
    {
        var before = Operations(baseline);
        var after = Operations(head);

        foreach (var (key, was) in before)
        {
            yield return new OperationPair(was, after.GetValueOrDefault(key));
        }

        foreach (var (key, now) in after)
        {
            if (!before.ContainsKey(key))
            {
                yield return new OperationPair(null, now);
            }
        }
    }

    /// <summary>
    /// Parameters by key. Path parameters pair by position, so renaming <c>{id}</c> to <c>{petId}</c> is not a change;
    /// header names are case-insensitive.
    /// </summary>
    public static Dictionary<string, ParameterContract> Parameters(OperationContract operation)
    {
        var pathNames = PathTemplate.ParameterNames(operation.Path).ToList();
        var map = new Dictionary<string, ParameterContract>(StringComparer.Ordinal);

        foreach (var parameter in operation.Parameters)
        {
            string key;
            if (parameter.Location == "path")
            {
                var index = pathNames.IndexOf(parameter.Name);
                key = index >= 0 ? $"path#{index}" : $"path:{parameter.Name}";
            }
            else if (parameter.Location == "header")
            {
                key = $"header:{parameter.Name.ToLowerInvariant()}";
            }
            else
            {
                key = $"{parameter.Location}:{parameter.Name}";
            }

            map.TryAdd(key, parameter);
        }

        return map;
    }

    /// <summary>Responses by upper-case status code (<c>4xx</c> and <c>4XX</c> are one); the first one wins.</summary>
    public static Dictionary<string, ResponseContract> Responses(OperationContract operation) =>
        operation.Responses
            .GroupBy(r => r.StatusCode.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>
    /// The properties a direction can see: read-only properties are not part of requests, write-only ones not of
    /// responses.
    /// </summary>
    public static Dictionary<string, PropertyContract> VisibleProperties(SchemaContract schema, Direction direction) =>
        schema.Properties
            .Where(p => direction == Direction.Request ? !p.Value.ReadOnly : !p.Value.WriteOnly)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>
    /// <c>oneOf</c>/<c>anyOf</c> alternatives by key: the component name, or a signature of type and property names for
    /// an inline one. Repeated keys get a <c>'</c> appended.
    /// </summary>
    public static Dictionary<string, SchemaContract> Variants(IReadOnlyList<SchemaContract> variants)
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

    /// <summary>The keys of both sides: the first side's in its order, then the keys only the second side has.</summary>
    public static IEnumerable<string> Union<T>(IReadOnlyDictionary<string, T> first, IReadOnlyDictionary<string, T> second) =>
        first.Keys.Concat(second.Keys.Where(key => !first.ContainsKey(key)));
}
