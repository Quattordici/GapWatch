using GapWatch.Core.Comparison;

namespace GapWatch.Core.Policy;

/// <summary>Everything <c>gapwatch.json</c> configures. Reading the file lives in GapWatch.Cli (<c>ConfigFile</c>).</summary>
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
