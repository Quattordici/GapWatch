using System.Text;
using System.Text.Json;
using GapWatch.Core.Policy;

namespace GapWatch.Cli;

/// <summary>
/// Reads and writes baseline files (<c>gapwatch-baseline.json</c>). Like <see cref="ConfigFile"/>, reading is strict:
/// unknown properties are errors. Writing is deterministic, so a regenerated baseline diffs cleanly in review.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "version": 1,
///   "justification": "Differences to the 2.3 release accepted when GapWatch was adopted.",
///   "findings": [
///     { "rule": "GW026", "operation": "GET /pets", "location": "response 200 application/json: $[].tag",
///       "message": "Optional response property 'tag' was removed." }
///   ]
/// }
/// </code>
/// </remarks>
public static class BaselineFile
{
    /// <summary>The only format version so far.</summary>
    public const int Version = 1;

    /// <summary>Justification for a new baseline; edit it in the file, it is kept when the file is rewritten.</summary>
    public const string DefaultJustification = "Existing differences accepted when the baseline was written.";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <exception cref="ConfigurationException">The file is missing or invalid.</exception>
    public static Baseline Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new ConfigurationException($"Baseline file not found: {path}");
        }

        try
        {
            return Parse(File.ReadAllText(path), path);
        }
        catch (IOException ex)
        {
            throw new ConfigurationException($"Could not read '{path}': {ex.Message}");
        }
    }

    /// <exception cref="ConfigurationException">The JSON is malformed or not a valid baseline.</exception>
    public static Baseline Parse(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(source);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"{source} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigurationException($"{source}: the baseline must be a JSON object.");
            }

            int? version = null;
            string? justification = null;
            var entries = new List<BaselineEntry>();

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "version":
                        version = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var v)
                            ? v
                            : throw new ConfigurationException($"{source}: $.version must be a whole number.");
                        break;
                    case "justification":
                        justification = String(property.Value, source, "$.justification");
                        break;
                    case "findings":
                        ReadFindings(property.Value, source, entries);
                        break;
                    default:
                        throw new ConfigurationException(
                            $"{source}: unknown property '{property.Name}'. Expected 'version', 'justification' or 'findings'.");
                }
            }

            if (version != Version)
            {
                throw new ConfigurationException(
                    version is null
                        ? $"{source}: $.version is missing; this GapWatch reads version {Version}."
                        : $"{source}: baseline version {version} is not supported; this GapWatch reads version {Version}.");
            }

            if (string.IsNullOrWhiteSpace(justification))
            {
                throw new ConfigurationException($"{source}: $.justification is missing or blank. Say why these findings are accepted.");
            }

            return new Baseline(justification, entries);
        }
    }

    /// <summary>Writes <paramref name="baseline"/> as indented UTF-8 JSON with LF line endings.</summary>
    public static void Save(string path, Baseline baseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(baseline);

        File.WriteAllText(path, Serialize(baseline), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string Serialize(Baseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a file, never embedded in HTML
        }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", Version);
            json.WriteString("justification", baseline.Justification);
            json.WriteStartArray("findings");
            foreach (var entry in baseline.Entries)
            {
                json.WriteStartObject();
                json.WriteString("rule", entry.RuleId);
                json.WriteString("operation", entry.Operation);
                json.WriteString("location", entry.Location);
                if (entry.Message is not null)
                {
                    json.WriteString("message", entry.Message);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static void ReadFindings(JsonElement array, string source, List<BaselineEntry> entries)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigurationException($"{source}: $.findings must be an array.");
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var path = $"$.findings[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigurationException($"{source}: {path} must be an object.");
            }

            string? rule = null, operation = null, location = null, message = null;
            foreach (var property in item.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                switch (property.Name)
                {
                    case "rule":
                        rule = String(property.Value, source, propertyPath);
                        break;
                    case "operation":
                        operation = String(property.Value, source, propertyPath);
                        break;
                    case "location":
                        location = String(property.Value, source, propertyPath);
                        break;
                    case "message":
                        message = String(property.Value, source, propertyPath);
                        break;
                    default:
                        throw new ConfigurationException(
                            $"{source}: unknown property '{propertyPath}'. Expected 'rule', 'operation', 'location' or 'message'.");
                }
            }

            if (rule is null || operation is null || location is null)
            {
                throw new ConfigurationException($"{source}: {path} needs 'rule', 'operation' and 'location'.");
            }

            entries.Add(new BaselineEntry(rule, operation, location, message));
        }
    }

    private static string String(JsonElement value, string source, string path) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ConfigurationException($"{source}: {path} must be a string.");
}
