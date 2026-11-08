using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    public static CodeMap Load(string directory) => FromSources(SourceCompilation.ReadDirectory(directory));

    /// <summary>Builds a map from in-memory sources: <c>(path, text)</c> pairs. The path is used verbatim in locations.</summary>
    public static CodeMap FromSources(IEnumerable<(string Path, string Text)> sources)
    {
        var compilation = SourceCompilation.Compile(sources);

        var endpoints = new List<Endpoint>();
        var types = new TypeIndex();
        foreach (var tree in compilation.SyntaxTrees)
        {
            endpoints.AddRange(EndpointScanner.Scan(tree, compilation).Select(e => new Endpoint(
                e.Method,
                e.Route,
                ToCodeLocation(e.Location),
                BoundParameters(e.Parameters))));
            types.Add(tree, compilation.GetSemanticModel(tree));
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

        if (finding.At is FindingLocation.Parameter parameter
            && endpoint.Parameters.TryGetValue(parameter.Name, out var parameterLocation))
        {
            return parameterLocation;
        }

        return endpoint.Location;
    }

    private Endpoint? FindEndpoint(string operation)
    {
        if (OperationIdentity.Parse(operation) is not { } identity)
        {
            return null;
        }

        var key = RouteTemplate.Key(identity.Method, identity.Path);
        return _endpoints.TryGetValue(key, out var matches) && matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Attributes that say where a value comes from; their <c>Name</c> overrides the parameter name.</summary>
    private static readonly HashSet<string> BindingAttributes = new(StringComparer.Ordinal)
    {
        "FromQuery", "FromRoute", "FromHeader", "FromForm",
    };

    /// <summary>Parameters that never appear as OpenAPI parameters.</summary>
    private static readonly HashSet<string> NonParameterAttributes = new(StringComparer.Ordinal)
    {
        "FromBody", "FromServices", "FromKeyedServices", "AsParameters",
    };

    private static Dictionary<string, CodeLocation> BoundParameters(IEnumerable<ParameterSyntax> parameters)
    {
        var result = new Dictionary<string, CodeLocation>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            var attributes = SyntaxHelpers.Attributes(parameter.AttributeLists).ToList();
            if (attributes.Any(a => NonParameterAttributes.Contains(SyntaxHelpers.Name(a)))
                || parameter.Type?.ToString() is "CancellationToken" or "HttpContext" or "HttpRequest" or "HttpResponse")
            {
                continue;
            }

            var name = attributes
                .Where(a => BindingAttributes.Contains(SyntaxHelpers.Name(a)))
                .Select(a => SyntaxHelpers.Property(a, "Name") is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
                    ? literal.Token.ValueText
                    : null)
                .FirstOrDefault(n => n is not null)
                ?? parameter.Identifier.ValueText;

            result.TryAdd(name, ToCodeLocation(parameter.Identifier.GetLocation()));
        }

        return result;
    }

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
