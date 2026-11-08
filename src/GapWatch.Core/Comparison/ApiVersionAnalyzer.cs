using System.Text.RegularExpressions;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;

namespace GapWatch.Core.Comparison;

/// <summary>How API versions are recognized in a contract. The defaults match Asp.Versioning.</summary>
/// <param name="PathSegment">
/// Pattern for a path segment that carries the version, e.g. <c>/v1/pets</c>. Null to not look at paths.
/// </param>
/// <param name="Parameters">
/// Query or header parameters whose enum lists the versions (case-insensitive), e.g. <c>api-version</c>.
/// </param>
/// <param name="MediaTypeParameter">Media type parameter that carries the version, e.g. <c>v</c> in <c>application/json;v=2.0</c>. Null to not look at media types.</param>
public sealed record VersioningOptions(Regex? PathSegment, IReadOnlyList<string> Parameters, string? MediaTypeParameter)
{
    /// <summary>The default <c>VVV</c> URL segment format with a <c>v</c> prefix.</summary>
    public const string DefaultPathSegment = @"^v\d+(\.\d+)?$";

    /// <summary><c>v1</c> or <c>v1.2</c> path segments, an <c>api-version</c> parameter, and the <c>v</c> media type parameter.</summary>
    public static VersioningOptions Default { get; } = new(PathPattern(DefaultPathSegment), ["api-version"], "v");

    /// <summary>
    /// Version detection off, except for operations that carry <see cref="OperationContract.ApiVersion"/> because they
    /// come from one document per version.
    /// </summary>
    public static VersioningOptions Disabled { get; } = new(null, [], null) { Enabled = false };

    /// <summary>False when <c>"versioning": false</c> was configured.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Compiles a path segment pattern the way configuration supplies it: case-insensitive, with a match timeout.</summary>
    /// <exception cref="ArgumentException">The pattern is not a valid regular expression.</exception>
    public static Regex PathPattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>
/// Turns per-operation findings about versioned operations into version findings (GW060 to GW063), so that retiring
/// API version 1 reads as one decision instead of one removed operation per endpoint.
/// </summary>
/// <remarks>
/// <para>
/// A version is read from each operation, the first source that has one: <see cref="OperationContract.ApiVersion"/>
/// (one document per version), the path segment, the enum of a version parameter, then the version parameter of its
/// media types. Versions compare without a leading <c>v</c>, case-insensitively, and with a trailing <c>.0</c>
/// ignored, so <c>v1</c>, <c>1</c> and <c>1.0</c> are the same version.
/// </para>
/// <para>
/// A version is removed only when no head operation has it any more. For the operations that lose a removed version,
/// GW060 (or GW061 when every base operation of the version was deprecated) replaces the GW001, the GW023 on the version
/// parameter and the GW043 on the versioned media types. An added version replaces GW003 (GW062), and a newly deprecated
/// version replaces GW002 (GW063). One finding is still reported per operation, so counts and exit codes stay per
/// operation; <see cref="Finding.ApiVersion"/> lets reports group them.
/// </para>
/// <para>
/// All four rules are about the version a client sends (path, parameter, media type, or the document it targets), so
/// they are request-side by nature. It runs after comparison and before the policy, so severity overrides,
/// suppressions and baselines apply to the version findings.
/// </para>
/// </remarks>
public static class ApiVersionAnalyzer
{
    /// <summary>Rewrites <paramref name="result"/>, the comparison of <paramref name="baseline"/> with <paramref name="head"/>.</summary>
    public static ComparisonResult Apply(
        ApiContract baseline, ApiContract head, ComparisonResult result, VersioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        var before = Describe(baseline, options);
        var after = Describe(head, options);
        if (before.Count == 0 && after.Count == 0)
        {
            return result;
        }

        var display = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var version in before.Concat(after).SelectMany(o => o.Versions))
        {
            display.TryAdd(version.Key, version.Text);
        }

        var baseVersions = VersionSet(before);
        var headVersions = VersionSet(after);
        var removed = baseVersions.Except(headVersions).ToHashSet(StringComparer.Ordinal);
        var added = headVersions.Except(baseVersions).ToHashSet(StringComparer.Ordinal);
        var wasDeprecated = Deprecated(before);
        var nowDeprecated = Deprecated(after);
        var newlyDeprecated = nowDeprecated.Intersect(baseVersions).Except(wasDeprecated).ToHashSet(StringComparer.Ordinal);

        var findings = result.Findings.ToList();
        var replacements = new List<(RuleDescriptor Rule, string Version, string Operation, OperationContract? Head)>();
        var headByKey = after.GroupBy(o => o.Operation.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var baseByKey = before.GroupBy(o => o.Operation.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Removed versions: the operation (or its version parameter value, or its versioned media types) is gone.
        foreach (var was in before)
        {
            headByKey.TryGetValue(was.Operation.Key, out var now);
            var lost = was.Keys.Except(now?.Keys ?? []).ToList();
            if (lost.Count == 0 || !lost.All(removed.Contains))
            {
                continue;
            }

            var operation = now?.Operation.Display ?? was.Operation.Display;
            findings.RemoveAll(f => f.Operation == operation && Replaced(f, was, lost, now is null));
            foreach (var version in lost)
            {
                var rule = wasDeprecated.Contains(version) ? RuleCatalog.DeprecatedApiVersionRemoved : RuleCatalog.ApiVersionRemoved;
                replacements.Add((rule, version, operation, now?.Operation));
            }
        }

        // Added versions: a new operation, or a new value of a version parameter or media type.
        foreach (var now in after)
        {
            baseByKey.TryGetValue(now.Operation.Key, out var was);
            var gained = now.Keys.Except(was?.Keys ?? []).ToList();
            if (gained.Count == 0 || !gained.All(added.Contains))
            {
                continue;
            }

            if (was is null)
            {
                findings.RemoveAll(f => f.Rule == RuleCatalog.OperationAdded && f.Operation == now.Operation.Display);
            }

            replacements.AddRange(gained.Select(v => (RuleCatalog.ApiVersionAdded, v, now.Operation.Display, (OperationContract?)now.Operation)));
        }

        // Newly deprecated versions: replace the GW002 of operations whose every version is now deprecated.
        foreach (var now in after.Where(o => o.Keys.Count > 0 && o.Keys.All(newlyDeprecated.Contains)))
        {
            var operation = now.Operation.Display;
            if (findings.RemoveAll(f => f.Rule == RuleCatalog.OperationDeprecated && f.Operation == operation) > 0)
            {
                replacements.AddRange(now.Keys.Select(v => (RuleCatalog.ApiVersionDeprecated, v, operation, (OperationContract?)now.Operation)));
            }
        }

        foreach (var group in replacements.GroupBy(r => (r.Rule, r.Version)))
        {
            var (rule, version) = group.Key;
            var text = display[version];
            var count = group.Select(r => r.Operation).Distinct(StringComparer.Ordinal).Count();
            var message = Message(rule, text, count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var replacement in group.Where(r => seen.Add(r.Operation)))
            {
                findings.Add(new Finding(rule, replacement.Operation, "operation", message)
                {
                    ApiVersion = text,
                    Position = replacement.Head?.Position,
                    Document = replacement.Head?.Position?.File ?? replacement.Head?.Document,
                });
            }
        }

        return new ComparisonResult(FindingOrder.Sort(findings), result.Suppressed, result.UnusedSuppressions);
    }

    /// <summary>Canonical form for comparing versions: <c>V1.0</c> becomes <c>1</c>.</summary>
    public static string Canonical(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var text = version.Trim().ToLowerInvariant();
        if (text.Length > 1 && text[0] == 'v' && char.IsAsciiDigit(text[1]))
        {
            text = text[1..];
        }

        while (text.EndsWith(".0", StringComparison.Ordinal) && text.Length > 2)
        {
            text = text[..^2];
        }

        return text;
    }

    private static string Message(RuleDescriptor rule, string version, int count)
    {
        var operations = count == 1 ? "1 operation" : $"{count} operations";
        return rule == RuleCatalog.ApiVersionRemoved ? $"API version '{version}' was removed without being deprecated first ({operations})."
            : rule == RuleCatalog.DeprecatedApiVersionRemoved ? $"API version '{version}' was removed; it was deprecated in the base contract ({operations})."
            : rule == RuleCatalog.ApiVersionAdded ? $"API version '{version}' was added ({operations})."
            : $"API version '{version}' was marked deprecated ({operations}).";
    }

    /// <summary>Whether <paramref name="finding"/> on a base operation is one of the findings a lost version replaces.</summary>
    private static bool Replaced(Finding finding, Versioned was, IReadOnlyList<string> lost, bool operationRemoved)
    {
        if (finding.Rule == RuleCatalog.OperationRemoved)
        {
            return operationRemoved;
        }

        if (finding.Rule == RuleCatalog.EnumValueRemoved && was.Source == Source.Parameter)
        {
            return was.Parameter is { } parameter
                   && finding.Location == $"parameter '{parameter.Name}' ({parameter.Location})";
        }

        if (finding.Rule == RuleCatalog.MediaTypeRemoved && was.Source == Source.MediaType)
        {
            return was.Versions.Any(v => lost.Contains(v.Key) && v.MediaTypes.Any(m => finding.Location.Contains($"({m})", StringComparison.Ordinal)
                                                                                           || finding.Message.Contains($"'{m}'", StringComparison.Ordinal)));
        }

        return false;
    }

    private static HashSet<string> VersionSet(IEnumerable<Versioned> operations) =>
        operations.SelectMany(o => o.Keys).ToHashSet(StringComparer.Ordinal);

    /// <summary>Versions whose every operation is deprecated.</summary>
    private static HashSet<string> Deprecated(IReadOnlyList<Versioned> operations) =>
        operations
            .SelectMany(o => o.Keys.Select(v => (Version: v, o.Operation.Deprecated)))
            .GroupBy(x => x.Version, StringComparer.Ordinal)
            .Where(g => g.All(x => x.Deprecated))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static List<Versioned> Describe(ApiContract contract, VersioningOptions options) =>
        contract.Operations
            .Select(o => Detect(o, options))
            .OfType<Versioned>()
            .ToList();

    private static Versioned? Detect(OperationContract operation, VersioningOptions options)
    {
        if (operation.ApiVersion is { } documentVersion)
        {
            return new Versioned(operation, Source.Document, [Version.Of(documentVersion)], null);
        }

        if (!options.Enabled)
        {
            return null;
        }

        if (options.PathSegment is { } pattern
            && operation.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(pattern.IsMatch) is { } segment)
        {
            return new Versioned(operation, Source.Path, [Version.Of(segment)], null);
        }

        var parameter = operation.Parameters.FirstOrDefault(p =>
            p.Location is "query" or "header"
            && p.Schema.EnumValues.Count > 0
            && options.Parameters.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
        if (parameter is not null)
        {
            var versions = parameter.Schema.EnumValues.Where(v => v != "null").Select(Version.Of).DistinctBy(v => v.Key).ToList();
            return versions.Count == 0 ? null : new Versioned(operation, Source.Parameter, versions, parameter);
        }

        if (options.MediaTypeParameter is { } name)
        {
            var mediaTypes = (operation.RequestBody?.Content.Keys ?? [])
                .Concat(operation.Responses.SelectMany(r => r.Content.Keys))
                .Select(m => (MediaType: m, Version: MediaTypeVersion(m, name)))
                .Where(x => x.Version is not null)
                .GroupBy(x => Canonical(x.Version!), StringComparer.Ordinal)
                .Select(g => new Version(g.Key, g.First().Version!, g.Select(x => x.MediaType).Distinct(StringComparer.Ordinal).ToList()))
                .ToList();
            if (mediaTypes.Count > 0)
            {
                return new Versioned(operation, Source.MediaType, mediaTypes, null);
            }
        }

        return null;
    }

    /// <summary>The value of parameter <paramref name="name"/> in a normalized media type such as <c>application/json;v=2.0</c>.</summary>
    private static string? MediaTypeVersion(string mediaType, string name) =>
        mediaType.Split(';').Skip(1)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && string.Equals(p[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim())
            .FirstOrDefault(v => v.Length > 0);

    private enum Source
    {
        Document,
        Path,
        Parameter,
        MediaType,
    }

    /// <param name="Key">Canonical form, used for comparison.</param>
    /// <param name="Text">The version as first written, used in messages.</param>
    /// <param name="MediaTypes">For media type versions, the media types that carry it.</param>
    private sealed record Version(string Key, string Text, IReadOnlyList<string> MediaTypes)
    {
        public static Version Of(string text) => new(Canonical(text), text, []);
    }

    private sealed record Versioned(
        OperationContract Operation, Source Source, IReadOnlyList<Version> Versions, ParameterContract? Parameter)
    {
        public IReadOnlyList<string> Keys { get; } = Versions.Select(v => v.Key).ToList();
    }
}
