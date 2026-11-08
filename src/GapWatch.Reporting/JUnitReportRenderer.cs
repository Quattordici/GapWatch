using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GapWatch.Core.Findings;

namespace GapWatch.Reporting;

/// <summary>
/// JUnit XML for CI systems that show test results but not SARIF (Azure DevOps "Tests" tab, Jenkins, GitLab).
/// </summary>
/// <remarks>
/// JUnit XML has no formal standard; this follows the common Ant/Jenkins shape that Azure DevOps and GitLab read.
/// <list type="bullet">
/// <item>One <c>testsuite</c> per run, one <c>testcase</c> per finding. <c>classname</c> is the operation and
/// <c>name</c> is <c>GW025 RequiredPropertyRemoved: &lt;location&gt;</c>, so results group by operation.</item>
/// <item>A finding at or above <see cref="ReportContext.FailOn"/> is a <c>failure</c>; below it the test case passes
/// and the message goes to <c>system-out</c>. The XML therefore agrees with the exit code.</item>
/// <item>Suppressed findings are <c>skipped</c>, with the justification as the message.</item>
/// <item>A run without findings still contains one passing test case, so the suite is never empty.</item>
/// </list>
/// </remarks>
public sealed class JUnitReportRenderer : IReportRenderer
{
    public string Render(ComparisonResult result, ReportContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var cases = new List<XElement>();
        var failures = 0;

        foreach (var finding in result.Findings)
        {
            var testCase = TestCase(finding);
            if (context.FailOn is { } threshold && finding.Severity >= threshold)
            {
                failures++;
                testCase.Add(new XElement(
                    "failure",
                    new XAttribute("message", finding.Message),
                    new XAttribute("type", $"{finding.Rule.Id} {finding.Severity}"),
                    Details(finding, context)));
            }
            else
            {
                testCase.Add(new XElement("system-out", $"{finding.Severity}: {Details(finding, context)}"));
            }

            cases.Add(testCase);
        }

        foreach (var (finding, suppression) in result.Suppressed)
        {
            var testCase = TestCase(finding);
            testCase.Add(new XElement("skipped", new XAttribute("message", $"Suppressed: {suppression.Justification}")));
            cases.Add(testCase);
        }

        if (cases.Count == 0)
        {
            cases.Add(new XElement(
                "testcase",
                new XAttribute("classname", "GapWatch"),
                new XAttribute("name", "No differences that affect consumers")));
        }

        var name = $"GapWatch: {context.BaseLabel} -> {context.HeadLabel}";
        var counts = new object[]
        {
            new XAttribute("tests", cases.Count),
            new XAttribute("failures", failures),
            new XAttribute("errors", 0),
            new XAttribute("skipped", result.Suppressed.Count),
        };

        var document = new XDocument(
            new XElement(
                "testsuites",
                new XAttribute("name", "GapWatch"),
                counts,
                new XElement("testsuite", new XAttribute("name", name), counts, cases)));

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            NewLineChars = "\n",
        };

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static XElement TestCase(Finding finding) =>
        new(
            "testcase",
            new XAttribute("classname", finding.Operation),
            new XAttribute("name", string.Create(CultureInfo.InvariantCulture, $"{finding.Rule.Id} {finding.Rule.Name}: {finding.Location}")));

    /// <summary>The failure text; the test name stays position-free so test history survives unrelated edits.</summary>
    private static string Details(Finding finding, ReportContext context)
    {
        var text = $"{finding.Operation} {finding.Location}: {finding.Message}";
        if (finding.Position is { } position)
        {
            text += $" (at {finding.Document ?? context.HeadLabel}:{position})";
        }

        return finding.Code is { } code ? $"{text} (code at {code})" : text;
    }
}
