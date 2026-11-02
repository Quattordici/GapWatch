using System.Text.Json;
using GapWatch.Core.Findings;
using GapWatch.Core.Policy;

namespace GapWatch.Cli;

/// <summary>The configuration file is missing, malformed or describes an invalid policy.</summary>
public sealed class ConfigurationException(string message) : Exception(message);

/// <summary>
/// Reads <c>gapwatch.json</c> into a <see cref="CompatibilityPolicy"/>. The format is deliberately small and strict:
/// unknown properties are errors so that a typo never silently disables a check.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "rules": { "GW004": "breaking", "GW003": "off" },
///   "suppressions": [
///     { "rule": "GW025", "operation": "GET /pets", "location": "response 200 application/json: $[].nickname",
///       "justification": "Never populated; removal agreed with all consumers." }
///   ]
/// }
/// </code>
/// Comments and trailing commas are allowed.
/// </remarks>
public static class ConfigFile
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads and validates a configuration file from disk.</summary>
    /// <exception cref="ConfigurationException">The file is missing or invalid.</exception>
    public static CompatibilityPolicy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new ConfigurationException($"Configuration file not found: {path}");
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

    /// <summary>Parses and validates configuration JSON. <paramref name="source"/> labels error messages.</summary>
    /// <exception cref="ConfigurationException">The JSON is malformed or describes an invalid policy.</exception>
    public static CompatibilityPolicy Parse(string json, string source)
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
            Expect(root, JsonValueKind.Object, source, "$");

            var overrides = new Dictionary<string, Severity>(StringComparer.Ordinal);
            var disabled = new HashSet<string>(StringComparer.Ordinal);
            var suppressions = new List<Suppression>();

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "rules":
                        ReadRules(property.Value, source, overrides, disabled);
                        break;
                    case "suppressions":
                        ReadSuppressions(property.Value, source, suppressions);
                        break;
                    default:
                        throw new ConfigurationException(
                            $"{source}: unknown property '{property.Name}'. Expected 'rules' or 'suppressions'.");
                }
            }

            var policy = new CompatibilityPolicy(overrides, disabled, suppressions);
            var problems = policy.Validate();
            return problems.Count == 0
                ? policy
                : throw new ConfigurationException($"{source}: {string.Join(" ", problems)}");
        }
    }

    private static void ReadRules(
        JsonElement rules, string source, Dictionary<string, Severity> overrides, HashSet<string> disabled)
    {
        Expect(rules, JsonValueKind.Object, source, "$.rules");

        foreach (var rule in rules.EnumerateObject())
        {
            var path = $"$.rules.{rule.Name}";
            Expect(rule.Value, JsonValueKind.String, source, path);

            switch (rule.Value.GetString()!.ToLowerInvariant())
            {
                case "off":
                    disabled.Add(rule.Name);
                    break;
                case "info":
                    overrides[rule.Name] = Severity.Info;
                    break;
                case "warning":
                    overrides[rule.Name] = Severity.Warning;
                    break;
                case "breaking":
                    overrides[rule.Name] = Severity.Breaking;
                    break;
                default:
                    throw new ConfigurationException(
                        $"{source}: {path} has value '{rule.Value.GetString()}'. Use 'breaking', 'warning', 'info' or 'off'.");
            }
        }
    }

    private static void ReadSuppressions(JsonElement array, string source, List<Suppression> suppressions)
    {
        Expect(array, JsonValueKind.Array, source, "$.suppressions");

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var path = $"$.suppressions[{index++}]";
            Expect(item, JsonValueKind.Object, source, path);

            string? rule = null, operation = null, location = null, justification = null;
            foreach (var property in item.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                switch (property.Name)
                {
                    case "rule":
                        rule = ReadString(property.Value, source, propertyPath);
                        break;
                    case "operation":
                        operation = ReadString(property.Value, source, propertyPath);
                        break;
                    case "location":
                        location = ReadString(property.Value, source, propertyPath);
                        break;
                    case "justification":
                        justification = ReadString(property.Value, source, propertyPath);
                        break;
                    default:
                        throw new ConfigurationException(
                            $"{source}: unknown property '{propertyPath}'. Expected 'rule', 'operation', 'location' or 'justification'.");
                }
            }

            if (rule is null)
            {
                throw new ConfigurationException($"{source}: {path} needs a 'rule'.");
            }

            suppressions.Add(new Suppression(rule, operation, location, justification ?? string.Empty));
        }
    }

    private static string ReadString(JsonElement value, string source, string path)
    {
        Expect(value, JsonValueKind.String, source, path);
        return value.GetString()!;
    }

    private static void Expect(JsonElement element, JsonValueKind kind, string source, string path)
    {
        if (element.ValueKind != kind)
        {
            var expected = kind.ToString().ToLowerInvariant();
            var article = expected[0] is 'a' or 'o' ? "an" : "a";
            throw new ConfigurationException(
                $"{source}: {path} must be {article} {expected}, not {element.ValueKind.ToString().ToLowerInvariant()}.");
        }
    }
}
