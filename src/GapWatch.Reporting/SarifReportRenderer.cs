using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GapWatch.Core.Findings;

namespace GapWatch.Reporting;

/// <summary>
/// SARIF 2.1.0 log for code-scanning dashboards (GitHub code scanning, the Azure DevOps SARIF viewer, IDE viewers).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every rule carries <c>shortDescription</c>, <c>fullDescription</c> and <c>help</c>, which GitHub code scanning
/// requires.</item>
/// <item>Severity maps to <c>level</c>: Breaking is <c>error</c>, Warning is <c>warning</c>, Info is <c>note</c>.
/// <c>defaultConfiguration.level</c> carries the rule's default; <c>result.level</c> the effective (possibly overridden) one.</item>
/// <item>Every result points at the head document (line 1, column 1, because readers do not track positions yet; GitHub
/// code scanning requires all four region coordinates) and carries a
/// logical location (<c>GET /pets</c> plus the location inside it), which is what identifies the change.</item>
/// <item>Suppressed findings are emitted with <c>suppressions[].kind = "external"</c> and the justification, so viewers
/// show them as suppressed instead of dropping them. Unused suppressions become tool execution notifications.</item>
/// <item><c>partialFingerprints</c> hash rule id, operation and location, so a finding keeps its identity across runs
/// even when its message wording changes.</item>
/// </list>
/// </remarks>
public sealed class SarifReportRenderer : IReportRenderer
{
    /// <summary>Key under <c>partialFingerprints</c>. Bump the suffix if the fingerprint input ever changes.</summary>
    public const string FingerprintKey = "gapwatchFinding/v1";

    private const string InformationUri = "https://github.com/Quattordici/GapWatch";
    private const string RulesHelpUri = InformationUri + "/blob/main/docs/rules.md";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a file, never embedded in HTML
    };

    public string Render(ComparisonResult result, ReportContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var ruleIndex = RuleCatalog.All
            .Select((rule, index) => (rule.Id, index))
            .ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("$schema", "https://json.schemastore.org/sarif-2.1.0.json");
            json.WriteString("version", "2.1.0");
            json.WriteStartArray("runs");
            json.WriteStartObject();

            WriteTool(json, context);
            WriteInvocation(json, result);
            WriteArtifacts(json, context);

            json.WriteStartArray("results");
            foreach (var finding in result.Findings)
            {
                WriteResult(json, finding, null, context, ruleIndex);
            }

            foreach (var (finding, suppression) in result.Suppressed)
            {
                WriteResult(json, finding, suppression.Justification, context, ruleIndex);
            }

            json.WriteEndArray();

            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>SARIF level for a severity.</summary>
    public static string Level(Severity severity) => severity switch
    {
        Severity.Breaking => "error",
        Severity.Warning => "warning",
        _ => "note",
    };

    /// <summary>Stable identity of a finding: SHA-256 over rule id, operation and location.</summary>
    public static string Fingerprint(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var input = string.Join('\n', finding.Rule.Id, finding.Operation, finding.Location);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// Turns a file label into a SARIF <c>artifactLocation.uri</c>: absolute paths become <c>file://</c> URIs,
    /// relative paths keep forward slashes and are percent-encoded per segment.
    /// </summary>
    public static string ArtifactUri(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        if (Path.IsPathFullyQualified(label))
        {
            return new Uri(label).AbsoluteUri;
        }

        return string.Join('/', label.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
    }

    private static void WriteTool(Utf8JsonWriter json, ReportContext context)
    {
        json.WriteStartObject("tool");
        json.WriteStartObject("driver");
        json.WriteString("name", "GapWatch");
        if (context.ToolVersion is { } version)
        {
            json.WriteString("semanticVersion", version);
        }

        json.WriteString("informationUri", InformationUri);
        json.WriteStartArray("rules");
        foreach (var rule in RuleCatalog.All)
        {
            json.WriteStartObject();
            json.WriteString("id", rule.Id);
            json.WriteString("name", rule.Name);
            WriteText(json, "shortDescription", rule.Name);
            WriteText(json, "fullDescription", rule.Description);
            json.WriteString("helpUri", RulesHelpUri);
            json.WriteStartObject("help");
            json.WriteString("text", $"{rule.Description} See {RulesHelpUri}");
            json.WriteString("markdown", $"**{rule.Id} {rule.Name}** ({rule.DefaultSeverity} by default)\n\n{rule.Description}\n\n[All GapWatch rules]({RulesHelpUri})");
            json.WriteEndObject();
            json.WriteStartObject("defaultConfiguration");
            json.WriteString("level", Level(rule.DefaultSeverity));
            json.WriteEndObject();
            json.WriteStartObject("properties");
            json.WriteStartArray("tags");
            json.WriteStringValue("api-compatibility");
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteInvocation(Utf8JsonWriter json, ComparisonResult result)
    {
        json.WriteStartArray("invocations");
        json.WriteStartObject();
        json.WriteBoolean("executionSuccessful", true);
        if (result.UnusedSuppressions.Count > 0)
        {
            json.WriteStartArray("toolExecutionNotifications");
            foreach (var suppression in result.UnusedSuppressions)
            {
                json.WriteStartObject();
                json.WriteString("level", "warning");
                WriteText(json, "message", $"Suppression '{suppression.Describe()}' matched no finding and can be removed.");
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
        json.WriteEndArray();
    }

    private static void WriteArtifacts(Utf8JsonWriter json, ReportContext context)
    {
        json.WriteStartArray("artifacts");
        foreach (var (label, role) in new[] { (context.BaseLabel, "referencedOnCommandLine"), (context.HeadLabel, "analysisTarget") })
        {
            json.WriteStartObject();
            json.WriteStartObject("location");
            json.WriteString("uri", ArtifactUri(label));
            json.WriteEndObject();
            json.WriteStartArray("roles");
            json.WriteStringValue(role);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteResult(
        Utf8JsonWriter json, Finding finding, string? justification, ReportContext context, Dictionary<string, int> ruleIndex)
    {
        json.WriteStartObject();
        json.WriteString("ruleId", finding.Rule.Id);
        json.WriteNumber("ruleIndex", ruleIndex[finding.Rule.Id]);
        json.WriteString("level", Level(finding.Severity));
        WriteText(json, "message", $"{finding.Operation}: {finding.Message}");

        json.WriteStartArray("locations");
        json.WriteStartObject();
        json.WriteStartObject("physicalLocation");
        json.WriteStartObject("artifactLocation");
        json.WriteString("uri", ArtifactUri(context.HeadLabel));
        json.WriteNumber("index", 1);
        json.WriteEndObject();
        json.WriteStartObject("region");
        // GitHub code scanning requires a complete region; readers do not track positions yet (v0.3).
        json.WriteNumber("startLine", 1);
        json.WriteNumber("startColumn", 1);
        json.WriteNumber("endLine", 1);
        json.WriteNumber("endColumn", 2);
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteStartArray("logicalLocations");
        json.WriteStartObject();
        json.WriteString("name", finding.Location);
        json.WriteString("fullyQualifiedName", $"{finding.Operation} > {finding.Location}");
        json.WriteString("kind", "member");
        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndArray();

        json.WriteStartObject("partialFingerprints");
        json.WriteString(FingerprintKey, Fingerprint(finding));
        json.WriteEndObject();

        if (justification is not null)
        {
            json.WriteStartArray("suppressions");
            json.WriteStartObject();
            json.WriteString("kind", "external");
            json.WriteString("status", "accepted");
            json.WriteString("justification", justification);
            json.WriteEndObject();
            json.WriteEndArray();
        }

        json.WriteStartObject("properties");
        json.WriteString("operation", finding.Operation);
        json.WriteString("location", finding.Location);
        json.WriteString("severity", finding.Severity.ToString());
        if (finding.Origin is { } origin)
        {
            json.WriteString("component", origin.Component);
            json.WriteString("componentPath", origin.Path);
        }

        json.WriteEndObject();

        json.WriteEndObject();
    }

    private static void WriteText(Utf8JsonWriter json, string name, string text)
    {
        json.WriteStartObject(name);
        json.WriteString("text", text);
        json.WriteEndObject();
    }
}
