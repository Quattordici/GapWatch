using System.Text.RegularExpressions;
using GapWatch.Core.Model;

namespace GapWatch.Roslyn;

/// <summary>Turns ASP.NET Core route templates into the form OpenAPI paths use, so the two can be matched.</summary>
internal static partial class RouteTemplate
{
    /// <summary>
    /// Matching key for an operation: method plus path with constraints, defaults, optional markers and catch-all stars
    /// removed, parameter names erased, and case ignored (ASP.NET Core routing is case-insensitive).
    /// </summary>
    public static string Key(string method, string route) =>
        $"{method.ToUpperInvariant()} {PathTemplate.Normalize(ToOpenApiPath(route)).ToLowerInvariant()}";

    /// <summary><c>api//pets/{id:int?}/</c> becomes <c>/api/pets/{id}</c>.</summary>
    public static string ToOpenApiPath(string route)
    {
        var path = Parameter().Replace(route, m => $"{{{m.Groups["name"].Value}}}");
        path = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return path;
    }

    /// <summary>Joins a controller-level and an action-level template the way attribute routing does.</summary>
    public static string Combine(string? prefix, string? template)
    {
        if (template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)))
        {
            return template.TrimStart('~');
        }

        if (string.IsNullOrEmpty(prefix))
        {
            return template ?? string.Empty;
        }

        return string.IsNullOrEmpty(template) ? prefix : $"{prefix.TrimEnd('/')}/{template}";
    }

    /// <summary>
    /// Appends a minimal API pattern to a <c>MapGroup</c> prefix. Unlike attribute routing, a leading slash does not make
    /// the pattern absolute: <c>MapGroup("/pets").MapGet("/{id}")</c> is <c>/pets/{id}</c>.
    /// </summary>
    public static string Append(string? prefix, string pattern) =>
        string.IsNullOrEmpty(prefix) ? pattern : $"{prefix.TrimEnd('/')}/{pattern.TrimStart('/')}";

    /// <summary>Replaces <c>[controller]</c>, <c>[action]</c> and <c>[area]</c> tokens (case-insensitive).</summary>
    public static string ReplaceTokens(string template, string controller, string action, string? area)
    {
        var result = template
            .Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", action, StringComparison.OrdinalIgnoreCase);
        return area is null ? result : result.Replace("[area]", area, StringComparison.OrdinalIgnoreCase);
    }

    // {name}, {name:int}, {name?}, {name=default}, {*name}, {**name:constraint}
    [GeneratedRegex(@"\{\*{0,2}(?<name>[^}:=?]+)[^}]*\}")]
    private static partial Regex Parameter();
}
