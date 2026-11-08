using GapWatch.Core.Findings;
using GapWatch.Core.Model;

namespace GapWatch.Sdk;

/// <summary>One schema node in the baseline and the head, at the same place in an operation.</summary>
/// <param name="At">Where the node is, ready to report: <c>parameter 'q' (query): $[]</c>, <c>response 200 application/json: $.name</c>.</param>
/// <param name="Before">The node in the baseline, or null when it is new (an added property, parameter or media type).</param>
/// <param name="After">The node in the head, or null when it is gone.</param>
/// <param name="Direction">
/// <see cref="Direction.Request"/> for parameters and request bodies (consumers send them), <see cref="Direction.Response"/>
/// for responses (consumers read them). Whether a change breaks a consumer depends on it.
/// </param>
public sealed record SchemaPair(FindingLocation At, SchemaContract? Before, SchemaContract? After, Direction Direction);

/// <summary>
/// Walks the schemas of an operation pair side by side, the way the built-in rules compare them, so a plugin rule can be
/// direction-aware without pairing anything itself.
/// </summary>
/// <remarks>
/// Every node is visited once, root first: parameters, then request body media types, then responses and their media
/// types, each followed by its properties (<c>$.name</c>), array items (<c>$[]</c>) and <c>oneOf</c>/<c>anyOf</c>
/// alternatives (<c>$&lt;Cat&gt;</c>). A side that is missing is null, and the walk continues into the side that exists.
/// A read-only property is not part of a request and a write-only one not of a response, so neither is visited there.
/// The walk does not descend into a reference cycle.
/// </remarks>
public static class SchemaWalker
{
    /// <summary>Every schema node of the operation, in both contracts.</summary>
    public static IEnumerable<SchemaPair> Walk(OperationPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);

        var beforeParameters = pair.Baseline is null ? [] : Pairing.Parameters(pair.Baseline);
        var afterParameters = pair.Head is null ? [] : Pairing.Parameters(pair.Head);
        foreach (var key in Pairing.Union(beforeParameters, afterParameters))
        {
            var was = beforeParameters.GetValueOrDefault(key);
            var now = afterParameters.GetValueOrDefault(key);
            var parameter = now ?? was!;
            foreach (var node in Walk(was?.Schema, now?.Schema, Direction.Request, path => new FindingLocation.Parameter(parameter.Name, parameter.Location, path)))
            {
                yield return node;
            }
        }

        var beforeBody = pair.Baseline?.RequestBody?.Content ?? new Dictionary<string, SchemaContract>();
        var afterBody = pair.Head?.RequestBody?.Content ?? new Dictionary<string, SchemaContract>();
        foreach (var mediaType in Pairing.Union(beforeBody, afterBody))
        {
            foreach (var node in Walk(beforeBody.GetValueOrDefault(mediaType), afterBody.GetValueOrDefault(mediaType), Direction.Request, path => new FindingLocation.RequestContent(mediaType, path)))
            {
                yield return node;
            }
        }

        var beforeResponses = pair.Baseline is null ? [] : Pairing.Responses(pair.Baseline);
        var afterResponses = pair.Head is null ? [] : Pairing.Responses(pair.Head);
        foreach (var status in Pairing.Union(beforeResponses, afterResponses))
        {
            var was = beforeResponses.GetValueOrDefault(status)?.Content ?? new Dictionary<string, SchemaContract>();
            var now = afterResponses.GetValueOrDefault(status)?.Content ?? new Dictionary<string, SchemaContract>();
            foreach (var mediaType in Pairing.Union(was, now))
            {
                foreach (var node in Walk(was.GetValueOrDefault(mediaType), now.GetValueOrDefault(mediaType), Direction.Response, path => new FindingLocation.ResponseContent(status, mediaType, path)))
                {
                    yield return node;
                }
            }
        }
    }

    /// <summary>
    /// Every node of two schemas compared in <paramref name="direction"/>, starting at the root (<c>$</c>).
    /// <paramref name="at"/> turns a schema path into a location.
    /// </summary>
    public static IEnumerable<SchemaPair> Walk(
        SchemaContract? before, SchemaContract? after, Direction direction, Func<string, FindingLocation> at)
    {
        ArgumentNullException.ThrowIfNull(at);

        return Walk(before, after, direction, at, FindingLocation.Root);
    }

    private static IEnumerable<SchemaPair> Walk(
        SchemaContract? before, SchemaContract? after, Direction direction, Func<string, FindingLocation> at, string path)
    {
        if (before is null && after is null)
        {
            yield break;
        }

        yield return new SchemaPair(at(path), before, after, direction);

        if (before?.IsRecursiveReference == true || after?.IsRecursiveReference == true)
        {
            yield break;
        }

        var beforeProperties = before is null ? [] : Pairing.VisibleProperties(before, direction);
        var afterProperties = after is null ? [] : Pairing.VisibleProperties(after, direction);
        foreach (var name in Pairing.Union(beforeProperties, afterProperties).Order(StringComparer.Ordinal))
        {
            foreach (var node in Walk(beforeProperties.GetValueOrDefault(name)?.Schema, afterProperties.GetValueOrDefault(name)?.Schema, direction, at, $"{path}.{name}"))
            {
                yield return node;
            }
        }

        foreach (var node in Walk(before?.Items, after?.Items, direction, at, $"{path}[]"))
        {
            yield return node;
        }

        var beforeVariants = before is null ? [] : Pairing.Variants(before.Variants);
        var afterVariants = after is null ? [] : Pairing.Variants(after.Variants);
        foreach (var key in Pairing.Union(beforeVariants, afterVariants).Order(StringComparer.Ordinal))
        {
            foreach (var node in Walk(beforeVariants.GetValueOrDefault(key), afterVariants.GetValueOrDefault(key), direction, at, $"{path}<{key}>"))
            {
                yield return node;
            }
        }
    }
}
