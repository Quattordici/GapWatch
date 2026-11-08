using GapWatch.Cli;
using GapWatch.Core.Findings;
using GapWatch.Reporting;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>
/// Locks the surfaces that docs/compatibility.md promises to keep stable. A failure here means a change that needs a
/// major release (or, for a new rule, an entry added at the end of the list): update deliberately, never to get green.
/// </summary>
public sealed class PublicContractTests
{
    /// <summary>Every rule ever released: id, name and default severity. Append new rules; never edit or remove a line.</summary>
    private static readonly string[] ReleasedRules =
    [
        "GW001 OperationRemoved Breaking",
        "GW002 OperationDeprecated Info",
        "GW003 OperationAdded Info",
        "GW004 OperationIdChanged Warning",
        "GW010 RequiredParameterAdded Breaking",
        "GW011 ParameterBecameRequired Breaking",
        "GW012 ParameterRemoved Warning",
        "GW020 TypeChanged Breaking",
        "GW021 FormatChanged Breaking",
        "GW022 NullabilityChanged Breaking",
        "GW023 EnumValueRemoved Breaking",
        "GW024 EnumValueAdded Warning",
        "GW025 RequiredPropertyRemoved Breaking",
        "GW026 PropertyRemoved Warning",
        "GW027 RequiredPropertyAdded Breaking",
        "GW028 PropertyBecameRequired Breaking",
        "GW029 PropertyBecameOptional Breaking",
        "GW030 VariantRemoved Breaking",
        "GW031 VariantAdded Warning",
        "GW032 ValueRangeNarrowed Breaking",
        "GW033 ValueRangeWidened Warning",
        "GW034 PatternAdded Breaking",
        "GW035 PatternChanged Warning",
        "GW036 AdditionalPropertiesRestricted Warning",
        "GW037 DiscriminatorChanged Breaking",
        "GW040 RequiredRequestBodyAdded Breaking",
        "GW041 RequestBodyBecameRequired Breaking",
        "GW042 RequestBodyRemoved Warning",
        "GW043 MediaTypeRemoved Breaking",
        "GW044 SuccessResponseRemoved Breaking",
        "GW045 ResponseRemoved Info",
        "GW050 SecurityAdded Breaking",
        "GW051 SecurityRequirementRemoved Warning",
        "GW052 SecurityRequirementsReplaced Breaking",
        "GW060 ApiVersionRemoved Breaking",
        "GW061 DeprecatedApiVersionRemoved Warning",
        "GW062 ApiVersionAdded Info",
        "GW063 ApiVersionDeprecated Info",
    ];

    [Test]
    public void Released_rules_keep_their_id_name_and_default_severity()
    {
        var current = RuleCatalog.All.ToDictionary(r => r.Id, r => $"{r.Id} {r.Name} {r.DefaultSeverity}", StringComparer.Ordinal);

        foreach (var released in ReleasedRules)
        {
            var id = released[..5];
            current.ShouldContainKey(id, $"Rule {id} was removed. Rule ids are permanent.");
            current[id].ShouldBe(released);
        }

        RuleCatalog.All.Count.ShouldBe(ReleasedRules.Length, "A new rule needs its line added to ReleasedRules.");
    }

    [Test]
    public void Docs_rules_md_matches_the_catalog()
    {
        var path = Path.Combine(Golden.RepositoryRoot(), "docs", "rules.md");
        var table = string.Concat(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').SkipWhile(line => !line.StartsWith("| Rule |", StringComparison.Ordinal)).Select(line => line + "\n")).TrimEnd() + "\n";

        table.ShouldBe(RuleListRenderer.RenderMarkdown(), "Regenerate the table with 'gapwatch rules --format markdown'.");
    }

    [Test]
    public void Exit_codes_fingerprint_key_and_markdown_marker_are_stable()
    {
        $"{ExitCodes.Success} {ExitCodes.FindingsAtThreshold} {ExitCodes.Error}".ShouldBe("0 1 2");
        SarifReportRenderer.FingerprintKey.ShouldBe("gapwatchFinding/v1");
        MarkdownReportRenderer.Marker.ShouldBe("<!-- gapwatch-report -->");
    }

    [Test]
    public void Help_lists_every_released_option()
    {
        using var output = new StringWriter();
        GapWatchCli.Run(["--help"], output, TextWriter.Null);

        foreach (var option in new[] { "--format", "--output", "--fail-on", "--config", "--source", "--baseline", "--write-baseline", "--base-ref", "--version", "--help" })
        {
            output.ToString().ShouldContain(option);
        }
    }
}
