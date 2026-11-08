using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using Microsoft.CodeAnalysis;

namespace GapWatch.Roslyn;

/// <summary>
/// Maps findings to the C# code behind the head contract: operations to controller actions and minimal API endpoints,
/// parameters to action or handler parameters, and component schemas to DTO types and properties.
/// </summary>
/// <remarks>
/// The code is read from source files only: no MSBuild, no restore and no build, so it works on any checkout. The files
/// are compiled together against the running .NET runtime, which resolves the project's own types (base classes,
/// property types, constants). ASP.NET Core is not referenced, so its attributes and <c>Map*</c> calls are recognized by
/// name. Mapping is best effort: when something cannot be matched unambiguously, the finding simply has no code location.
/// </remarks>
public sealed class CodeMap
{
    private readonly Dictionary<string, List<OperationContract>> _operations;
    private readonly TypeIndex _types;

    private CodeMap(ApiContract contract, TypeIndex types)
    {
        Contract = contract;
        _types = types;
        _operations = contract.Operations
            .GroupBy(o => RouteTemplate.Key(o.Method, o.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The contract read from the code by <see cref="CSharpContractReader"/>: the same binding rules as
    /// <c>--input csharp</c>, so an endpoint or parameter the reader understands is one the map can place.
    /// </summary>
    public ApiContract Contract { get; }

    /// <summary>Reads every <c>.cs</c> file under <paramref name="directory"/>, skipping <c>bin</c>, <c>obj</c>, <c>.git</c> and <c>node_modules</c>.</summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public static CodeMap Load(string directory) => FromSources(SourceCompilation.ReadDirectory(directory));

    /// <summary>Builds a map from in-memory sources: <c>(path, text)</c> pairs. The path is used verbatim in locations.</summary>
    public static CodeMap FromSources(IEnumerable<(string Path, string Text)> sources)
    {
        var compilation = SourceCompilation.Compile(sources);

        var types = new TypeIndex();
        foreach (var tree in compilation.SyntaxTrees)
        {
            types.Add(tree, compilation.GetSemanticModel(tree));
        }

        return new CodeMap(CSharpContractReader.Read(compilation), types);
    }

    /// <summary>Returns a copy of <paramref name="result"/> in which every finding the map can place has a <see cref="Finding.Code"/>.</summary>
    public ComparisonResult Annotate(ComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ComparisonResult(
            result.Findings.Select(Annotate).ToList(),
            result.Suppressed.Select(s => s with { Finding = Annotate(s.Finding) }).ToList(),
            result.UnusedSuppressions);

        Finding Annotate(Finding finding) => Locate(finding) is { } code ? finding with { Code = code } : finding;
    }

    /// <summary>
    /// The code location for a finding: the DTO type or property for a change inside a component schema, the parameter
    /// for a parameter change, otherwise the endpoint. Null when nothing matches unambiguously, or for a removed operation.
    /// </summary>
    public CodeLocation? Locate(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        if (finding.Origin is { } origin && _types.Locate(origin.Component, origin.Path) is { } schemaLocation)
        {
            return schemaLocation;
        }

        if (finding.Rule == RuleCatalog.OperationRemoved || FindOperations(finding.Operation) is not { } operations)
        {
            return null;
        }

        if (finding.At is FindingLocation.Parameter parameter
            && Unique(operations
                .SelectMany(o => o.Parameters)
                .Where(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Position)) is { } parameterLocation)
        {
            return parameterLocation;
        }

        return Unique(operations.Select(o => o.Position));
    }

    /// <summary>
    /// The operations read from the code that a finding's operation matches, ignoring case, parameter names and the
    /// API version (one endpoint mapped to several versions is still one place in the code). Null when there are none.
    /// </summary>
    private List<OperationContract>? FindOperations(string operation) =>
        OperationIdentity.Parse(operation) is { } identity
        && _operations.TryGetValue(RouteTemplate.Key(identity.Method, identity.Path), out var matches)
            ? matches
            : null;

    /// <summary>The one code location the positions share, or null when there is none or more than one.</summary>
    private static CodeLocation? Unique(IEnumerable<SourcePosition?> positions)
    {
        var distinct = positions.OfType<SourcePosition>().Distinct().Take(2).ToList();
        return distinct is [{ File: { } file } only] ? new CodeLocation(file, only.Line, only.Column) : null;
    }

    internal static CodeLocation ToCodeLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new CodeLocation(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}
