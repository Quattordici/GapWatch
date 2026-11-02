using System.Text.RegularExpressions;

namespace GapWatch.Core.Model;

/// <summary>
/// A normalized, source-independent description of an HTTP API surface.
/// Every reader (OpenAPI JSON, OpenAPI YAML, Roslyn later) compiles into this model.
/// </summary>
public sealed record ApiContract(string? Title, string? Version, IReadOnlyList<OperationContract> Operations);

/// <summary>One HTTP operation (method + path) and everything a consumer can observe about it.</summary>
/// <param name="Method">Upper-case HTTP method.</param>
/// <param name="Path">Path template exactly as declared, e.g. <c>/pets/{id}</c>.</param>
/// <param name="OperationId">Declared operationId; generated clients are often named after it.</param>
/// <param name="Deprecated">Whether the operation is marked deprecated.</param>
/// <param name="Parameters">Path, query, header and cookie parameters (path-level parameters already merged).</param>
/// <param name="RequestBody">The request body, if any.</param>
/// <param name="Responses">Declared responses keyed by status code.</param>
/// <param name="Security">
/// Effective security alternatives (OR of ANDs) as canonical strings, e.g. <c>oauth2[read,write]+apiKey[]</c>.
/// The literal alternative <c>none</c> means unauthenticated access is allowed.
/// </param>
public sealed record OperationContract(
    string Method,
    string Path,
    string? OperationId,
    bool Deprecated,
    IReadOnlyList<ParameterContract> Parameters,
    RequestBodyContract? RequestBody,
    IReadOnlyList<ResponseContract> Responses,
    IReadOnlyList<string> Security)
{
    /// <summary>Human-readable identity, e.g. <c>GET /pets/{id}</c>.</summary>
    public string Display => $"{Method} {Path}";

    /// <summary>
    /// Identity used to pair operations across versions. Path parameter names are erased so that
    /// renaming <c>{id}</c> to <c>{petId}</c> is not reported as a removal.
    /// </summary>
    public string Key => $"{Method} {PathTemplate.Normalize(Path)}";
}

/// <summary>A single declared parameter.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Location">One of <c>path</c>, <c>query</c>, <c>header</c>, <c>cookie</c>.</param>
/// <param name="Required">Whether the parameter must be supplied.</param>
/// <param name="Schema">Value schema.</param>
public sealed record ParameterContract(string Name, string Location, bool Required, SchemaContract Schema);

/// <summary>A request body: whether it is mandatory and which media types are accepted.</summary>
public sealed record RequestBodyContract(bool Required, IReadOnlyDictionary<string, SchemaContract> Content);

/// <summary>A declared response: status code (<c>200</c>, <c>4XX</c>, <c>DEFAULT</c>) and its media types.</summary>
public sealed record ResponseContract(string StatusCode, IReadOnlyDictionary<string, SchemaContract> Content);

/// <summary>Helpers for OpenAPI path templates.</summary>
public static partial class PathTemplate
{
    [GeneratedRegex(@"\{([^}]*)\}")]
    private static partial Regex Placeholder();

    /// <summary>Replaces every <c>{name}</c> placeholder with <c>{}</c>.</summary>
    public static string Normalize(string path) => Placeholder().Replace(path, "{}");

    /// <summary>Returns placeholder names in order of appearance.</summary>
    public static IReadOnlyList<string> ParameterNames(string path) =>
        Placeholder().Matches(path).Select(m => m.Groups[1].Value).ToList();
}
