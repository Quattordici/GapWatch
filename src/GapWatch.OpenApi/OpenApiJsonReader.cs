using System.Text.Json;
using GapWatch.Core;
using GapWatch.Core.Model;

namespace GapWatch.OpenApi;

/// <summary>
/// Reads OpenAPI 3.0 / 3.1 documents in JSON into an <see cref="ApiContract"/> using only <c>System.Text.Json</c>.
/// Supports local <c>$ref</c>s; external references and Swagger 2.0 are rejected with a clear message.
/// </summary>
public sealed class OpenApiJsonReader : IContractReader
{
    public bool CanRead(string fileName) => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads the contract and records where each element is declared (<see cref="SourcePosition"/>).</summary>
    public ApiContract Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var utf8 = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        if (utf8.Span.StartsWith("\uFEFF"u8))
        {
            utf8 = utf8[3..];
        }

        try
        {
            using var document = JsonDocument.Parse(utf8, DocumentOptions);
            var positions = DocumentPositions.FromJson(
                utf8,
                new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return Read(document.RootElement, positions);
        }
        catch (JsonException ex)
        {
            throw new ContractReadException($"The document is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Builds the contract from an already parsed document, taking positions from <paramref name="positions"/>.</summary>
    internal static ApiContract Read(JsonElement root, DocumentPositions positions) => new Builder(root, positions).Build();

    private sealed class Builder(JsonElement root, DocumentPositions positions)
    {
        private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

        private static readonly HashSet<string> SchemaKeywords = new(StringComparer.Ordinal)
        {
            "type", "nullable", "properties", "items", "enum", "const", "allOf", "oneOf", "anyOf", "format", "required",
        };

        private readonly Dictionary<string, SchemaContract> _cache = new(StringComparer.Ordinal);
        private int _recursiveLeaves;

        public ApiContract Build()
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ContractReadException("The document root must be a JSON object.");
            }

            if (root.TryGetProperty("swagger", out _))
            {
                throw new ContractReadException(
                    "Swagger 2.0 documents are not supported. Convert the document to OpenAPI 3.x first.");
            }

            var version = GetString(root, "openapi");
            if (version is null || !version.StartsWith("3.", StringComparison.Ordinal))
            {
                throw new ContractReadException(
                    "Not an OpenAPI 3.x document: the 'openapi' property is missing or has an unsupported version.");
            }

            var title = root.TryGetProperty("info", out var info) ? GetString(info, "title") : null;
            var apiVersion = info.ValueKind == JsonValueKind.Object ? GetString(info, "version") : null;

            var operations = new List<OperationContract>();
            if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
            {
                foreach (var path in paths.EnumerateObject())
                {
                    if (!path.Name.StartsWith('/'))
                    {
                        continue; // specification extensions such as x-...
                    }

                    var item = Resolve(path.Value, DocumentPositions.Child("/paths", path.Name));
                    if (item.Element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var method in Methods)
                    {
                        if (item.Element.TryGetProperty(method, out var operation) && operation.ValueKind == JsonValueKind.Object)
                        {
                            operations.Add(ReadOperation(
                                method.ToUpperInvariant(),
                                path.Name,
                                item,
                                new Located(operation, DocumentPositions.Child(item.Pointer, method))));
                        }
                    }
                }
            }

            return new ApiContract(title, apiVersion, operations);
        }

        private OperationContract ReadOperation(string method, string path, Located item, Located operation)
        {
            RequestBodyContract? body = null;
            if (operation.Element.TryGetProperty("requestBody", out var bodyElement))
            {
                var resolved = Resolve(bodyElement, DocumentPositions.Child(operation.Pointer, "requestBody"));
                if (resolved.Element.ValueKind == JsonValueKind.Object)
                {
                    body = new RequestBodyContract(GetBool(resolved.Element, "required"), ReadContent(resolved))
                    {
                        Position = positions[resolved.Pointer],
                    };
                }
            }

            var responses = new List<ResponseContract>();
            if (operation.Element.TryGetProperty("responses", out var responsesElement)
                && responsesElement.ValueKind == JsonValueKind.Object)
            {
                var responsesPointer = DocumentPositions.Child(operation.Pointer, "responses");
                foreach (var response in responsesElement.EnumerateObject())
                {
                    if (response.Name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var resolved = Resolve(response.Value, DocumentPositions.Child(responsesPointer, response.Name));
                    var content = resolved.Element.ValueKind == JsonValueKind.Object
                        ? ReadContent(resolved)
                        : new Dictionary<string, SchemaContract>();
                    responses.Add(new ResponseContract(response.Name.ToUpperInvariant(), content)
                    {
                        Position = positions[resolved.Pointer],
                    });
                }
            }

            return new OperationContract(
                method,
                path,
                GetString(operation.Element, "operationId"),
                GetBool(operation.Element, "deprecated"),
                ReadParameters(item, operation),
                body,
                responses,
                ReadSecurity(operation.Element))
            {
                Position = positions[operation.Pointer],
            };
        }

        private List<ParameterContract> ReadParameters(Located item, Located operation)
        {
            // Operation-level parameters override path-level ones with the same (name, in).
            var merged = new Dictionary<(string Name, string In), ParameterContract>();

            foreach (var source in new[] { item, operation })
            {
                if (!source.Element.TryGetProperty("parameters", out var array) || array.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var arrayPointer = DocumentPositions.Child(source.Pointer, "parameters");
                var index = 0;
                foreach (var element in array.EnumerateArray())
                {
                    var parameter = Resolve(element, DocumentPositions.Child(arrayPointer, index++));
                    var name = GetString(parameter.Element, "name");
                    var location = GetString(parameter.Element, "in");
                    if (name is null || location is null)
                    {
                        continue;
                    }

                    var required = location == "path" || GetBool(parameter.Element, "required");
                    var schema = parameter.Element.TryGetProperty("schema", out var schemaElement)
                        ? ParseSchema(schemaElement, DocumentPositions.Child(parameter.Pointer, "schema"), [])
                        : ReadContent(parameter).Values.FirstOrDefault() ?? SchemaContract.Unspecified;

                    merged[(name, location)] = new ParameterContract(name, location, required, schema)
                    {
                        Position = positions[parameter.Pointer],
                    };
                }
            }

            return merged.Values.ToList();
        }

        private Dictionary<string, SchemaContract> ReadContent(Located holder)
        {
            var result = new Dictionary<string, SchemaContract>(StringComparer.Ordinal);
            if (!holder.Element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            var contentPointer = DocumentPositions.Child(holder.Pointer, "content");
            foreach (var media in content.EnumerateObject())
            {
                var mediaPointer = DocumentPositions.Child(contentPointer, media.Name);
                var schema = media.Value.ValueKind == JsonValueKind.Object
                             && media.Value.TryGetProperty("schema", out var schemaElement)
                    ? ParseSchema(schemaElement, DocumentPositions.Child(mediaPointer, "schema"), [])
                    : SchemaContract.Unspecified with { Position = positions[mediaPointer] };

                result.TryAdd(NormalizeMediaType(media.Name), schema);
            }

            return result;
        }

        private List<string> ReadSecurity(JsonElement operation)
        {
            JsonElement requirements;
            if (operation.TryGetProperty("security", out var own) && own.ValueKind == JsonValueKind.Array)
            {
                requirements = own;
            }
            else if (root.TryGetProperty("security", out var global) && global.ValueKind == JsonValueKind.Array)
            {
                requirements = global;
            }
            else
            {
                return [];
            }

            var alternatives = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var requirement in requirements.EnumerateArray())
            {
                if (requirement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var parts = requirement.EnumerateObject()
                    .Select(p => $"{p.Name}[{string.Join(",", Scopes(p.Value).OrderBy(s => s, StringComparer.Ordinal))}]")
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

                // An empty requirement object means "anonymous access is allowed".
                alternatives.Add(parts.Count == 0 ? "none" : string.Join("+", parts));
            }

            return alternatives.ToList();
        }

        private static IEnumerable<string> Scopes(JsonElement element) =>
            element.ValueKind == JsonValueKind.Array
                ? element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
                : [];

        // --- Schemas ------------------------------------------------------------------------------------------

        /// <summary>Parses the schema at <paramref name="pointer"/>; a <c>$ref</c>'d schema keeps its definition's position.</summary>
        private SchemaContract ParseSchema(JsonElement element, string pointer, HashSet<string> expanding)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return SchemaContract.Unspecified with { Position = positions[pointer] };
            }

            if (!element.TryGetProperty("$ref", out var referenceElement) || referenceElement.ValueKind != JsonValueKind.String)
            {
                return ParseSchemaBody(element, pointer, expanding);
            }

            var reference = referenceElement.GetString()!;
            var name = ComponentName(reference) ?? reference;

            if (_cache.TryGetValue(reference, out var cached))
            {
                return WithSiblings(cached with { ReferenceName = name }, element, pointer, expanding);
            }

            if (!expanding.Add(reference))
            {
                _recursiveLeaves++;
                return new SchemaContract { ReferenceName = name, IsRecursiveReference = true, Position = positions[pointer] };
            }

            try
            {
                var leavesBefore = _recursiveLeaves;
                var target = FollowPointer(reference);
                var parsed = ParseSchema(target.Element, target.Pointer, expanding) with { ReferenceName = name };

                // A subtree that never hit a cycle does not depend on where it was reached from, so it can be reused.
                if (_recursiveLeaves == leavesBefore)
                {
                    _cache[reference] = parsed;
                }

                return WithSiblings(parsed, element, pointer, expanding);
            }
            finally
            {
                expanding.Remove(reference);
            }
        }

        /// <summary>OpenAPI 3.1 allows keywords next to <c>$ref</c>; merge them in when present.</summary>
        private SchemaContract WithSiblings(SchemaContract resolved, JsonElement element, string pointer, HashSet<string> expanding) =>
            element.EnumerateObject().Any(p => SchemaKeywords.Contains(p.Name))
                ? Merge(ParseSchemaBody(element, pointer, expanding), resolved)
                : resolved;

        private SchemaContract ParseSchemaBody(JsonElement element, string pointer, HashSet<string> expanding)
        {
            var types = new SortedSet<string>(StringComparer.Ordinal);
            var nullable = GetBool(element, "nullable");

            if (element.TryGetProperty("type", out var typeElement))
            {
                if (typeElement.ValueKind == JsonValueKind.String)
                {
                    AddType(typeElement.GetString());
                }
                else if (typeElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in typeElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
                    {
                        AddType(entry.GetString());
                    }
                }
            }

            var required = new HashSet<string>(StringComparer.Ordinal);
            if (element.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in requiredElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
                {
                    required.Add(entry.GetString()!);
                }
            }

            var properties = new Dictionary<string, PropertyContract>(StringComparer.Ordinal);
            if (element.TryGetProperty("properties", out var propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object)
            {
                var propertiesPointer = DocumentPositions.Child(pointer, "properties");
                foreach (var property in propertiesElement.EnumerateObject())
                {
                    var propertyPointer = DocumentPositions.Child(propertiesPointer, property.Name);
                    properties[property.Name] = new PropertyContract(
                        ParseSchema(property.Value, propertyPointer, expanding),
                        required.Contains(property.Name),
                        HasFlag(property.Value, "readOnly"),
                        HasFlag(property.Value, "writeOnly"))
                    {
                        Position = positions[propertyPointer],
                    };
                }
            }

            SchemaContract? items = null;
            if (element.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Object)
            {
                items = ParseSchema(itemsElement, DocumentPositions.Child(pointer, "items"), expanding);
            }

            if (types.Count == 0 && properties.Count > 0)
            {
                types.Add("object");
            }

            if (types.Count == 0 && items is not null)
            {
                types.Add("array");
            }

            var variants = new List<SchemaContract>();
            foreach (var keyword in new[] { "oneOf", "anyOf" })
            {
                if (!element.TryGetProperty(keyword, out var alternatives) || alternatives.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var alternativesPointer = DocumentPositions.Child(pointer, keyword);
                var index = 0;
                foreach (var alternative in alternatives.EnumerateArray())
                {
                    var alternativePointer = DocumentPositions.Child(alternativesPointer, index++);
                    if (IsNullSchema(alternative))
                    {
                        nullable = true; // OpenAPI 3.1: anyOf: [X, {type: null}] means "X or null"
                    }
                    else
                    {
                        variants.Add(ParseSchema(alternative, alternativePointer, expanding));
                    }
                }
            }

            var own = new SchemaContract
            {
                Types = types.ToList(),
                Format = GetString(element, "format"),
                Nullable = nullable,
                EnumValues = ReadEnum(element),
                Properties = properties,
                Items = items,
                Variants = variants,
                Position = positions[pointer],
            };

            // A lone alternative with nothing else around it is just that schema (possibly nullable).
            if (variants.Count == 1 && types.Count == 0 && properties.Count == 0 && items is null && own.EnumValues.Count == 0
                && !element.TryGetProperty("allOf", out _))
            {
                return variants[0] with { Nullable = variants[0].Nullable || nullable };
            }

            if (element.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
            {
                var allOfPointer = DocumentPositions.Child(pointer, "allOf");
                var index = 0;
                foreach (var part in allOf.EnumerateArray())
                {
                    own = Merge(own, ParseSchema(part, DocumentPositions.Child(allOfPointer, index++), expanding));
                }

                // 'required' may name properties that were contributed by an allOf part.
                own = ApplyRequired(own, required);
            }

            return own;

            void AddType(string? value)
            {
                if (value is null)
                {
                    return;
                }

                var normalized = value.ToLowerInvariant();
                if (normalized == "null")
                {
                    nullable = true;
                }
                else
                {
                    types.Add(normalized);
                }
            }
        }

        private static SchemaContract ApplyRequired(SchemaContract schema, HashSet<string> required)
        {
            if (required.Count == 0 || schema.Properties.Count == 0)
            {
                return schema;
            }

            var properties = schema.Properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (var name in required.Where(properties.ContainsKey))
            {
                properties[name] = properties[name] with { Required = true };
            }

            return schema with { Properties = properties };
        }

        /// <summary>Combines two schemas as <c>allOf</c> would: union of properties, requirements and flags; first non-empty wins for scalars.</summary>
        private static SchemaContract Merge(SchemaContract a, SchemaContract b)
        {
            var properties = new Dictionary<string, PropertyContract>(StringComparer.Ordinal);
            foreach (var (name, property) in a.Properties)
            {
                properties[name] = property;
            }

            foreach (var (name, property) in b.Properties)
            {
                properties[name] = properties.TryGetValue(name, out var existing)
                    ? new PropertyContract(
                        Merge(existing.Schema, property.Schema),
                        existing.Required || property.Required,
                        existing.ReadOnly || property.ReadOnly,
                        existing.WriteOnly || property.WriteOnly)
                    {
                        Position = existing.Position ?? property.Position,
                    }
                    : property;
            }

            return new SchemaContract
            {
                Types = a.Types.Count > 0 ? a.Types : b.Types,
                Format = a.Format ?? b.Format,
                Nullable = a.Nullable || b.Nullable,
                EnumValues = a.EnumValues.Count > 0 ? a.EnumValues : b.EnumValues,
                Properties = properties,
                Items = a.Items is not null && b.Items is not null ? Merge(a.Items, b.Items) : a.Items ?? b.Items,
                Variants = [.. a.Variants, .. b.Variants],
                ReferenceName = a.ReferenceName ?? b.ReferenceName,
                Position = a.Position ?? b.Position,
            };
        }

        private static List<string> ReadEnum(JsonElement element)
        {
            var values = new SortedSet<string>(StringComparer.Ordinal);

            if (element.TryGetProperty("enum", out var enumElement) && enumElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in enumElement.EnumerateArray())
                {
                    values.Add(Canonical(value));
                }
            }

            if (element.TryGetProperty("const", out var constant))
            {
                values.Add(Canonical(constant));
            }

            return values.ToList();

            static string Canonical(JsonElement value) => value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Null => "null",
                _ => value.GetRawText(),
            };
        }

        private static bool IsNullSchema(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "null";

        // --- References and helpers -----------------------------------------------------------------------------

        /// <summary>Follows a (possibly chained) <c>$ref</c> and returns the final non-reference element.</summary>
        private Located Resolve(JsonElement element, string pointer)
        {
            var current = new Located(element, pointer);
            for (var hops = 0; hops < 32; hops++)
            {
                if (current.Element.ValueKind != JsonValueKind.Object
                    || !current.Element.TryGetProperty("$ref", out var reference)
                    || reference.ValueKind != JsonValueKind.String)
                {
                    return current;
                }

                current = FollowPointer(reference.GetString()!);
            }

            throw new ContractReadException("Too many chained $ref hops; the document probably contains a reference cycle.");
        }

        private Located FollowPointer(string reference)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                throw new ContractReadException(
                    $"Only local references are supported; found '{reference}'. Bundle the document into a single file first.");
            }

            var current = root;
            var pointer = string.Empty;
            foreach (var raw in reference[2..].Split('/'))
            {
                var segment = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                {
                    throw new ContractReadException($"Unresolvable reference '{reference}'.");
                }

                current = next;
                pointer = DocumentPositions.Child(pointer, segment);
            }

            return new Located(current, pointer);
        }

        private bool HasFlag(JsonElement element, string flag)
        {
            for (var hops = 0; hops < 32 && element.ValueKind == JsonValueKind.Object; hops++)
            {
                if (element.TryGetProperty(flag, out var value) && value.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (element.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
                {
                    element = FollowPointer(reference.GetString()!).Element;
                }
                else
                {
                    break;
                }
            }

            return false;
        }

        private static string? ComponentName(string reference)
        {
            const string Prefix = "#/components/schemas/";
            return reference.StartsWith(Prefix, StringComparison.Ordinal)
                ? Uri.UnescapeDataString(reference[Prefix.Length..])
                : null;
        }

        /// <summary>
        /// Canonical media type: type and parameter names lower-cased, values unquoted, parameters sorted by name.
        /// Only <c>charset</c> is dropped. Other parameters can select a different representation, such as an API
        /// version (<c>application/json;v=2.0</c>) or an OData profile, so they stay part of the media type. Dropping
        /// them would merge such entries and leave all but one uncompared.
        /// </summary>
        internal static string NormalizeMediaType(string mediaType)
        {
            var parts = mediaType.Split(';');
            var parameters = parts[1..]
                .Select(p => p.Split('=', 2))
                .Where(p => p[0].Trim().Length > 0)
                .Select(p => (Name: p[0].Trim().ToLowerInvariant(), Value: p.Length > 1 ? p[1].Trim().Trim('"') : string.Empty))
                .Where(p => p.Name != "charset")
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => $";{p.Name}={p.Value}");

            return parts[0].Trim().ToLowerInvariant() + string.Concat(parameters);
        }

        private static string? GetString(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool GetBool(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>A JSON element together with its pointer in the document.</summary>
    private readonly record struct Located(JsonElement Element, string Pointer);
}
