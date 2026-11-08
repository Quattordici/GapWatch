namespace GapWatch.Core.Model;

/// <summary>
/// Who an operation is: method, path and, in multi-document mode, API version. Owns the operation string
/// (<c>GET /pets/{id}</c>, <c>GET /pets [2.0]</c>), which is part of the public contract, and the key operations pair by.
/// </summary>
/// <param name="Method">Upper-case HTTP method.</param>
/// <param name="Path">Path template exactly as declared.</param>
/// <param name="ApiVersion">The version of the document the operation came from, or null for a single document.</param>
public sealed record OperationIdentity(string Method, string Path, string? ApiVersion = null)
{
    /// <summary>The stable operation string, e.g. <c>GET /pets/{id}</c> or <c>GET /pets/{id} [2.0]</c>.</summary>
    public string Display => ApiVersion is null ? $"{Method} {Path}" : $"{Method} {Path} [{ApiVersion}]";

    /// <summary>
    /// Identity used to pair operations across versions. Path parameter names are erased so that renaming <c>{id}</c>
    /// to <c>{petId}</c> is not reported as a removal.
    /// </summary>
    public string Key => ApiVersion is null
        ? $"{Method} {PathTemplate.Normalize(Path)}"
        : $"{Method} {PathTemplate.Normalize(Path)} [{ApiVersion}]";

    /// <summary>The <see cref="Display"/> string.</summary>
    public override string ToString() => Display;

    /// <summary>Reads an operation string back, the inverse of <see cref="Display"/>. Null when it is not one.</summary>
    public static OperationIdentity? Parse(string display)
    {
        ArgumentNullException.ThrowIfNull(display);

        var separator = display.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0 || separator == display.Length - 1)
        {
            return null;
        }

        var method = display[..separator];
        var path = display[(separator + 1)..];
        string? version = null;
        if (path.EndsWith(']') && path.LastIndexOf(" [", StringComparison.Ordinal) is var open and > 0)
        {
            version = path[(open + 2)..^1];
            path = path[..open];
        }

        return new OperationIdentity(method, path, version);
    }
}
