using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;

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
/// <item>Every result points at the head document, at the line and column of the changed element (or its closest
/// enclosing element that still exists; line 1 for a removed operation). GitHub code scanning requires all four region
/// coordinates. When a source map found the C# code behind the change, that code is the primary location and the
/// OpenAPI document a related location. Each result also carries a logical location (<c>GET /pets</c> plus the location inside it), which is what identifies the change.</item>
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
                WriteText(
                    json,
                    "message",
                    suppression.IsBaseline
                        ? $"Baseline entry '{suppression.Describe()}' matched no finding; rewrite the baseline to drop it."
                        : $"Suppression '{suppression.Describe()}' matched no finding and can be removed.");
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

        // With a code location, the C# code is the primary location (that is where the change is made, and where
        // pull request annotations are useful) and the OpenAPI document becomes a related location.
        var specRegion = finding.Position ?? new SourcePosition(1, 1);
        json.WriteStartArray("locations");
        json.WriteStartObject();
        if (finding.Code is { } code)
        {
            WritePhysicalLocation(json, ArtifactUri(code.File), artifactIndex: null, code.Line, code.Column);
        }
        else
        {
            WriteSpecLocation(json, finding, context, specRegion);
        }

        json.WriteStartArray("logicalLocations");
        json.WriteStartObject();
        json.WriteString("name", finding.Location);
        json.WriteString("fullyQualifiedName", $"{finding.Operation} > {finding.Location}");
        json.WriteString("kind", "member");
        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndArray();

        if (finding.Code is not null)
        {
            json.WriteStartArray("relatedLocations");
            json.WriteStartObject();
            json.WriteNumber("id", 1);
            WriteSpecLocation(json, finding, context, specRegion);
            WriteText(json, "message", "Where the change is in the OpenAPI document.");
            json.WriteEndObject();
            json.WriteEndArray();
        }

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

        if (finding.ApiVersion is { } apiVersion)
        {
            json.WriteString("apiVersion", apiVersion);
        }

        json.WriteEndObject();

        json.WriteEndObject();
    }

    /// <summary>
    /// The place in the head OpenAPI document: the single head document (artifact 1), or in multi-document mode the
    /// version document the operation is in, which is not one of the listed artifacts. A removed version is in no head
    /// document, so it falls back to the first one at 1:1, as a removed operation does in single-document mode: code
    /// scanning needs a file in the repository, and a folder is not one.
    /// </summary>
    private static void WriteSpecLocation(Utf8JsonWriter json, Finding finding, ReportContext context, SourcePosition region)
    {
        if ((finding.Document ?? context.FallbackHeadDocument) is { } document)
        {
            WritePhysicalLocation(json, ArtifactUri(document), artifactIndex: null, region.Line, region.Column);
        }
        else
        {
            WritePhysicalLocation(json, ArtifactUri(context.HeadLabel), artifactIndex: 1, region.Line, region.Column);
        }
    }

    /// <summary>
    /// A physical location with a one-character region; GitHub code scanning requires all four region coordinates.
    /// Without a position (a removed operation, or a reader without positions) callers pass line 1, column 1.
    /// </summary>
    private static void WritePhysicalLocation(Utf8JsonWriter json, string uri, int? artifactIndex, int line, int column)
    {
        json.WriteStartObject("physicalLocation");
        json.WriteStartObject("artifactLocation");
        json.WriteString("uri", uri);
        if (artifactIndex is { } index)
        {
            json.WriteNumber("index", index);
        }

        json.WriteEndObject();
        json.WriteStartObject("region");
        json.WriteNumber("startLine", line);
        json.WriteNumber("startColumn", column);
        json.WriteNumber("endLine", line);
        json.WriteNumber("endColumn", column + 1);
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
