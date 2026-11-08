using System.Text.Json;
using System.Text.RegularExpressions;
using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Policy;

namespace GapWatch.Cli;

/// <summary>The configuration file is missing, malformed or describes an invalid policy.</summary>
public sealed class ConfigurationException(string message) : Exception(message);

/// <summary>Everything <c>gapwatch.json</c> configures.</summary>
/// <param name="Policy">Severity overrides, disabled rules and suppressions, applied after comparison.</param>
/// <param name="Versioning">How API versions are recognized for GW060 to GW063.</param>
public sealed record GapWatchConfiguration(CompatibilityPolicy Policy, VersioningOptions Versioning)
{
    /// <summary>
    /// The configured failure threshold: <c>"breaking"</c>, <c>"warning"</c> or <c>"none"</c>, or null when the file does
    /// not set one. The <c>--fail-on</c> option wins over it.
    /// </summary>
    public string? FailOn { get; init; }

    /// <summary>No configuration file: the default policy and Asp.Versioning defaults.</summary>
    public static GapWatchConfiguration Default { get; } = new(CompatibilityPolicy.Default, VersioningOptions.Default);
}

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
///   ],
///   "versioning": { "pathSegment": "^v\\d+(\\.\\d+)?$", "parameters": ["api-version"], "mediaTypeParameter": "v" }
/// }
/// </code>
/// <c>"versioning": false</c> turns version detection off.
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
    public static CompatibilityPolicy Load(string path) => LoadConfiguration(path).Policy;

    /// <summary>Parses and validates configuration JSON into a policy. <paramref name="source"/> labels error messages.</summary>
    /// <exception cref="ConfigurationException">The JSON is malformed or describes an invalid policy.</exception>
    public static CompatibilityPolicy Parse(string json, string source) => ParseConfiguration(json, source).Policy;

    /// <summary>Loads and validates a configuration file from disk, including the <c>versioning</c> section.</summary>
    /// <exception cref="ConfigurationException">The file is missing or invalid.</exception>
    public static GapWatchConfiguration LoadConfiguration(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new ConfigurationException($"Configuration file not found: {path}");
        }

        try
        {
            return ParseConfiguration(File.ReadAllText(path), path);
        }
        catch (IOException ex)
        {
            throw new ConfigurationException($"Could not read '{path}': {ex.Message}");
        }
    }

    /// <summary>Parses and validates configuration JSON. <paramref name="source"/> labels error messages.</summary>
    /// <exception cref="ConfigurationException">The JSON is malformed or describes an invalid configuration.</exception>
    public static GapWatchConfiguration ParseConfiguration(string json, string source)
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
            var versioning = VersioningOptions.Default;
            string? failOn = null;

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
                    case "failOn":
                        failOn = ReadString(property.Value, source, "$.failOn").ToLowerInvariant();
                        if (failOn is not ("breaking" or "warning" or "none"))
                        {
                            throw new ConfigurationException(
                                $"{source}: $.failOn has value '{property.Value.GetString()}'. Use 'breaking', 'warning' or 'none'.");
                        }

                        break;
                    case "versioning":
                        versioning = ReadVersioning(property.Value, source);
                        break;
                    default:
                        throw new ConfigurationException(
                            $"{source}: unknown property '{property.Name}'. Expected 'rules', 'suppressions', 'failOn' or 'versioning'.");
                }
            }

            var policy = new CompatibilityPolicy(overrides, disabled, suppressions);
            var problems = policy.Validate();
            return problems.Count == 0
                ? new GapWatchConfiguration(policy, versioning) { FailOn = failOn }
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

    /// <summary>
    /// <c>false</c>, or an object whose keys are all optional. A key set to <c>null</c> turns that source off; a missing
    /// key keeps the default.
    /// </summary>
    private static VersioningOptions ReadVersioning(JsonElement element, string source)
    {
        if (element.ValueKind == JsonValueKind.False)
        {
            return VersioningOptions.Disabled;
        }

        Expect(element, JsonValueKind.Object, source, "$.versioning");

        var options = VersioningOptions.Default;
        foreach (var property in element.EnumerateObject())
        {
            var path = $"$.versioning.{property.Name}";
            var value = property.Value;
            switch (property.Name)
            {
                case "pathSegment":
                    options = options with { PathSegment = value.ValueKind == JsonValueKind.Null ? null : PathPattern(ReadString(value, source, path), source, path) };
                    break;
                case "parameters":
                    options = options with { Parameters = value.ValueKind == JsonValueKind.Null ? [] : ReadStrings(value, source, path) };
                    break;
                case "mediaTypeParameter":
                    options = options with { MediaTypeParameter = value.ValueKind == JsonValueKind.Null ? null : ReadString(value, source, path) };
                    break;
                default:
                    throw new ConfigurationException(
                        $"{source}: unknown property '{path}'. Expected 'pathSegment', 'parameters' or 'mediaTypeParameter'.");
            }
        }

        return options;
    }

    private static Regex PathPattern(string pattern, string source, string path)
    {
        try
        {
            return VersioningOptions.PathPattern(pattern);
        }
        catch (ArgumentException ex)
        {
            throw new ConfigurationException($"{source}: {path} is not a valid regular expression: {ex.Message}");
        }
    }

    private static List<string> ReadStrings(JsonElement array, string source, string path)
    {
        Expect(array, JsonValueKind.Array, source, path);
        return array.EnumerateArray().Select((item, i) => ReadString(item, source, $"{path}[{i}]")).ToList();
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
            DateOnly? expires = null;
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
                    case "expires":
                        var text = ReadString(property.Value, source, propertyPath);
                        expires = DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                            ? date
                            : throw new ConfigurationException($"{source}: {propertyPath} must be a date like 2026-12-31, not '{text}'.");
                        break;
                    default:
                        throw new ConfigurationException(
                            $"{source}: unknown property '{propertyPath}'. Expected 'rule', 'operation', 'location', 'justification' or 'expires'.");
                }
            }

            if (rule is null)
            {
                throw new ConfigurationException($"{source}: {path} needs a 'rule'.");
            }

            suppressions.Add(new Suppression(rule, operation, location, justification ?? string.Empty) { Expires = expires });
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
