using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Sdk;

namespace GapWatch.Core.Comparison;

/// <summary>
/// Where a <see cref="FindingLocation"/> is in the head operation: the parameter, request body, response, media type,
/// property or item it names, or the closest enclosing element the contract has a position for. Used for plugin
/// findings, whose positions come from the contract and never from the plugin, so they point where a built-in finding
/// at the same location would.
/// </summary>
internal static class LocationPositions
{
    public static SourcePosition? Find(OperationContract head, FindingLocation at)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(at);

        return at switch
        {
            FindingLocation.Parameter p => Parameter(head, p) is { } parameter
                ? Walk(parameter.Schema, p.SchemaPath, parameter.Position ?? head.Position)
                : head.Position,
            FindingLocation.RequestBody => head.RequestBody?.Position ?? head.Position,
            FindingLocation.RequestContent c => head.RequestBody is { } body && body.Content.TryGetValue(c.MediaType, out var schema)
                ? Walk(schema, c.SchemaPath, schema.Position ?? body.Position ?? head.Position)
                : head.RequestBody?.Position ?? head.Position,
            FindingLocation.Response r => Response(head, r.Status)?.Position ?? head.Position,
            FindingLocation.ResponseMediaType m => Media(head, m.Status, m.MediaType),
            FindingLocation.ResponseContent c => Response(head, c.Status) is { } response && response.Content.TryGetValue(c.MediaType, out var schema)
                ? Walk(schema, c.SchemaPath, schema.Position ?? response.Position ?? head.Position)
                : Response(head, c.Status)?.Position ?? head.Position,
            _ => head.Position,
        };
    }

    private static ParameterContract? Parameter(OperationContract head, FindingLocation.Parameter at) =>
        head.Parameters.FirstOrDefault(p => p.Location == at.In && string.Equals(
            p.Name, at.Name, at.In == "header" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private static ResponseContract? Response(OperationContract head, string status) =>
        Pairing.Responses(head).GetValueOrDefault(status.ToUpperInvariant());

    private static SourcePosition? Media(OperationContract head, string status, string mediaType)
    {
        var response = Response(head, status);
        return response is not null && response.Content.TryGetValue(mediaType, out var schema)
            ? schema.Position ?? response.Position ?? head.Position
            : response?.Position ?? head.Position;
    }

    /// <summary>
    /// Follows a schema path (<c>$.data[].name</c>, <c>$&lt;Cat&gt;.lives</c>) as far as the schema goes, keeping the
    /// last position seen.
    /// </summary>
    private static SourcePosition? Walk(SchemaContract schema, string path, SourcePosition? position)
    {
        var current = schema;
        var at = position;
        var index = path.StartsWith(FindingLocation.Root, StringComparison.Ordinal) ? 1 : 0;
        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                var end = NextSegment(path, index + 1);
                if (!current.Properties.TryGetValue(path[(index + 1)..end], out var property))
                {
                    return at;
                }

                at = property.Position ?? property.Schema.Position ?? at;
                current = property.Schema;
                index = end;
            }
            else if (path[index] == '[' && path.AsSpan(index).StartsWith("[]"))
            {
                if (current.Items is not { } items)
                {
                    return at;
                }

                at = items.Position ?? at;
                current = items;
                index += 2;
            }
            else if (path[index] == '<' && path.IndexOf('>', index) is var close and > 0)
            {
                if (!Pairing.Variants(current.Variants).TryGetValue(path[(index + 1)..close], out var variant))
                {
                    return at;
                }

                at = variant.Position ?? at;
                current = variant;
                index = close + 1;
            }
            else
            {
                return at;
            }
        }

        return at;
    }

    private static int NextSegment(string path, int start)
    {
        var end = path.IndexOfAny(['.', '[', '<'], start);
        return end < 0 ? path.Length : end;
    }
}
