using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
    private static readonly string[] ExcludedDirectories = ["bin", "obj", ".git", "node_modules"];

    private readonly Dictionary<string, List<Endpoint>> _endpoints;
    private readonly TypeIndex _types;

    private CodeMap(IReadOnlyList<Endpoint> endpoints, TypeIndex types)
    {
        Endpoints = endpoints;
        _types = types;
        _endpoints = endpoints
            .GroupBy(e => RouteTemplate.Key(e.Method, e.Route), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>Every endpoint found in the code, in source order.</summary>
    public IReadOnlyList<Endpoint> Endpoints { get; }

    /// <summary>Reads every <c>.cs</c> file under <paramref name="directory"/>, skipping <c>bin</c> and <c>obj</c>.</summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public static CodeMap Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {directory}");
        }

        var files = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .Select(path => (path.Replace('\\', '/'), File.ReadAllText(path)));

        return FromSources(files);
    }

    /// <summary>Builds a map from in-memory sources: <c>(path, text)</c> pairs. The path is used verbatim in locations.</summary>
    public static CodeMap FromSources(IEnumerable<(string Path, string Text)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, options, s.Path)).ToList();

        // Projects usually rely on SDK implicit usings, which a bare compilation does not have.
        trees.Add(CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading.Tasks;",
            options,
            "<implicit-usings>"));

        var compilation = CSharpCompilation.Create(
            "GapWatch.SourceMap",
            trees,
            RuntimeReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var endpoints = new List<Endpoint>();
        var types = new TypeIndex();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            endpoints.AddRange(EndpointScanner.Scan(tree, model));
            types.Add(tree, model);
        }

        return new CodeMap(endpoints, types);
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

        if (finding.Rule == RuleCatalog.OperationRemoved || FindEndpoint(finding.Operation) is not { } endpoint)
        {
            return null;
        }

        if (ParameterName(finding.Location) is { } parameter
            && endpoint.Parameters.TryGetValue(parameter, out var parameterLocation))
        {
            return parameterLocation;
        }

        return endpoint.Location;
    }

    private Endpoint? FindEndpoint(string operation)
    {
        var separator = operation.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var key = RouteTemplate.Key(operation[..separator], operation[(separator + 1)..]);
        return _endpoints.TryGetValue(key, out var matches) && matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The parameter name in a location such as <c>parameter 'status' (query)</c> or <c>parameter 'id' (path): $</c>.</summary>
    private static string? ParameterName(string location)
    {
        const string Prefix = "parameter '";
        if (!location.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = location.IndexOf('\'', Prefix.Length);
        return end > Prefix.Length ? location[Prefix.Length..end] : null;
    }

    private static readonly Lazy<IReadOnlyList<MetadataReference>> RuntimeReferences = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileName(path) is var name
                           && (name.StartsWith("System.", StringComparison.Ordinal)
                               || name is "System.dll" or "mscorlib.dll" or "netstandard.dll"))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList());

    internal static CodeLocation ToCodeLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new CodeLocation(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}

/// <summary>An HTTP endpoint found in code.</summary>
/// <param name="Method">Upper-case HTTP method.</param>
/// <param name="Route">Route template as declared, with constraints, e.g. <c>/api/pets/{id:int}</c>.</param>
/// <param name="Location">The action method name or the <c>Map*</c> call.</param>
/// <param name="Parameters">Bound parameter names (as the client sends them, case-insensitive) and where they are declared.</param>
public sealed record Endpoint(
    string Method,
    string Route,
    CodeLocation Location,
    IReadOnlyDictionary<string, CodeLocation> Parameters);
