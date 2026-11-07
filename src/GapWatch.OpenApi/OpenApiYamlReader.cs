using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GapWatch.Core;
using GapWatch.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GapWatch.OpenApi;

/// <summary>
/// Reads OpenAPI 3.x documents in YAML. The YAML is converted to the equivalent JSON tree and handed to
/// <see cref="OpenApiJsonReader"/>, so YAML and JSON documents are normalized by exactly the same code.
/// </summary>
/// <remarks>
/// OpenAPI asks for YAML 1.2 restricted to the JSON schema, so plain scalars are typed with the YAML 1.2 core schema:
/// <c>yes</c>, <c>no</c>, <c>on</c> and <c>off</c> are strings (YAML 1.1 parsers would read booleans), quoted scalars
/// are always strings, and <c>~</c> or an empty value is <c>null</c>. Anchors and aliases are expanded. Merge keys
/// (<c>&lt;&lt;</c>, a YAML 1.1 extension) and multi-document streams are rejected with a clear message.
/// </remarks>
public sealed partial class OpenApiYamlReader : IContractReader
{
    private const string StringTag = "tag:yaml.org,2002:str";
    private const int MaxDepth = 512;

    public bool CanRead(string fileName) =>
        fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    public ApiContract Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var positions = new DocumentPositions();
        var json = ToJson(stream, positions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            json.WriteTo(writer);
        }

        // Positions come from the YAML text, not from the intermediate JSON.
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        return OpenApiJsonReader.Read(document.RootElement, positions);
    }

    /// <summary>Parses a single YAML document into the JSON tree it denotes.</summary>
    /// <exception cref="ContractReadException">The YAML is invalid or uses features OpenAPI does not allow.</exception>
    public static JsonNode ToJson(Stream stream) => ToJson(stream, positions: null);

    /// <summary>As <see cref="ToJson(Stream)"/>, also recording where each member and item is declared.</summary>
    internal static JsonNode ToJson(Stream stream, DocumentPositions? positions)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var yaml = new YamlStream();
        try
        {
            using var reader = new StreamReader(stream);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new ContractReadException($"The document is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            // Some malformed inputs (for example an unterminated flow sequence) make the scanner fail without a position.
            throw new ContractReadException($"The document is not valid YAML: {ex.Message}", ex);
        }

        if (yaml.Documents.Count != 1)
        {
            throw new ContractReadException(
                $"Expected exactly one YAML document, found {yaml.Documents.Count}. Split multi-document files first.");
        }

        var root = yaml.Documents[0].RootNode;
        positions?.Add(string.Empty, Position(root.Start));
        return Convert(root, string.Empty, positions, depth: 0)
               ?? throw new ContractReadException("The YAML document is empty.");
    }

    private static JsonNode? Convert(YamlNode node, string pointer, DocumentPositions? positions, int depth)
    {
        // Aliases are resolved to the anchored node, so an alias to an ancestor would recurse forever.
        if (depth > MaxDepth)
        {
            throw new ContractReadException(
                $"The YAML document nests deeper than {MaxDepth} levels; it probably contains an alias that refers to its own ancestor.");
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (keyNode, valueNode) in mapping.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: { } key })
                    {
                        throw new ContractReadException($"{Position(keyNode)}: mapping keys must be plain strings.");
                    }

                    if (key == "<<" && keyNode is YamlScalarNode { Style: ScalarStyle.Plain })
                    {
                        throw new ContractReadException(
                            $"{Position(keyNode)}: YAML merge keys ('<<') are a YAML 1.1 extension that OpenAPI does not allow. Expand the merged mapping.");
                    }

                    if (obj.ContainsKey(key))
                    {
                        throw new ContractReadException($"{Position(keyNode)}: duplicate key '{key}'.");
                    }

                    var member = DocumentPositions.Child(pointer, key);
                    positions?.Add(member, Position(keyNode.Start));
                    obj[key] = Convert(valueNode, member, positions, depth + 1);
                }

                return obj;

            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var item in sequence.Children)
                {
                    var itemPointer = DocumentPositions.Child(pointer, array.Count);
                    positions?.Add(itemPointer, Position(item.Start));
                    array.Add(Convert(item, itemPointer, positions, depth + 1));
                }

                return array;

            case YamlScalarNode scalar:
                return Scalar(scalar);

            default:
                throw new ContractReadException($"{Position(node)}: unsupported YAML node.");
        }
    }

    /// <summary>Types a scalar with the YAML 1.2 core schema. Quoted or <c>!!str</c>-tagged scalars stay strings.</summary>
    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain || (!scalar.Tag.IsEmpty && !scalar.Tag.IsNonSpecific && scalar.Tag.Value == StringTag))
        {
            return JsonValue.Create(text);
        }

        if (text is "" or "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (text is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (text is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (DecimalInteger().IsMatch(text))
        {
            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? JsonValue.Create(value)
                : JsonValue.Create(decimal.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }

        if (text.StartsWith("0x", StringComparison.Ordinal) && HexInteger().IsMatch(text))
        {
            return JsonValue.Create(long.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        }

        if (text.StartsWith("0o", StringComparison.Ordinal) && OctalInteger().IsMatch(text))
        {
            return JsonValue.Create(System.Convert.ToInt64(text[2..], 8));
        }

        if (Float().IsMatch(text)
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        // Everything else, including .inf and .nan which JSON cannot represent, is a string.
        return JsonValue.Create(text);
    }

    private static string Position(YamlNode node) => $"Line {node.Start.Line}, column {node.Start.Column}";

    private static SourcePosition Position(Mark mark) => new((int)mark.Line, (int)mark.Column);

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex DecimalInteger();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")]
    private static partial Regex HexInteger();

    [GeneratedRegex(@"^0o[0-7]+$")]
    private static partial Regex OctalInteger();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();
}
