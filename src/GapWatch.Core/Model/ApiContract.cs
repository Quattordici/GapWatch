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
    /// <summary>Where the operation is declared in the source document, when the reader tracks positions.</summary>
    public SourcePosition? Position { get; init; }

    /// <summary>
    /// The API version of the document the operation was read from, when one document per version is compared
    /// (multi-document mode). It is part of the identity, so <c>GET /pets</c> in version 1 and in version 2 are
    /// different operations. Null for a single document.
    /// </summary>
    public string? ApiVersion { get; init; }

    /// <summary>The document the operation was read from, in multi-document mode. Null for a single document.</summary>
    public string? Document { get; init; }

    /// <summary>Method, path and API version; owns the operation string and the pairing key.</summary>
    public OperationIdentity Identity => new(Method, Path, ApiVersion);

    /// <summary>Human-readable identity, e.g. <c>GET /pets/{id}</c>, or <c>GET /pets/{id} [2.0]</c> with an <see cref="ApiVersion"/>.</summary>
    public string Display => Identity.Display;

    /// <summary>Identity used to pair operations across versions (see <see cref="OperationIdentity.Key"/>).</summary>
    public string Key => Identity.Key;
}

/// <summary>A single declared parameter.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Location">One of <c>path</c>, <c>query</c>, <c>header</c>, <c>cookie</c>.</param>
/// <param name="Required">Whether the parameter must be supplied.</param>
/// <param name="Schema">Value schema.</param>
public sealed record ParameterContract(string Name, string Location, bool Required, SchemaContract Schema)
{
    /// <summary>Where the parameter is declared in the source document, when the reader tracks positions.</summary>
    public SourcePosition? Position { get; init; }
}

/// <summary>A request body: whether it is mandatory and which media types are accepted.</summary>
public sealed record RequestBodyContract(bool Required, IReadOnlyDictionary<string, SchemaContract> Content)
{
    /// <summary>Where the request body is declared in the source document, when the reader tracks positions.</summary>
    public SourcePosition? Position { get; init; }
}

/// <summary>A declared response: status code (<c>200</c>, <c>4XX</c>, <c>DEFAULT</c>) and its media types.</summary>
public sealed record ResponseContract(string StatusCode, IReadOnlyDictionary<string, SchemaContract> Content)
{
    /// <summary>Where the response is declared in the source document, when the reader tracks positions.</summary>
    public SourcePosition? Position { get; init; }
}

/// <summary>
/// A place in the document a contract was read from: 1-based line and column (in UTF-16 code units, as editors count).
/// It is plain data, so any reader can provide it; the file it refers to is known to whoever loaded the document,
/// unless <see cref="File"/> names it.
/// </summary>
public sealed record SourcePosition(int Line, int Column)
{
    /// <summary>
    /// The file, for a contract read from several files (C# source), where each element can live in another one. Null
    /// means the document the contract was read from.
    /// </summary>
    public string? File { get; init; }

    /// <summary><c>line:column</c>.</summary>
    public override string ToString() => $"{Line}:{Column}";
}

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
