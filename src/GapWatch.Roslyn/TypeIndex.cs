using GapWatch.Core.Findings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GapWatch.Roslyn;

/// <summary>
/// Finds the C# type behind a component schema and the property behind a schema path such as <c>$.owner.name</c>.
/// </summary>
/// <remarks>
/// Swashbuckle and <c>Microsoft.AspNetCore.OpenApi</c> both name a non-generic type's schema after the type's simple
/// name, so that is the match. Generic types are skipped because the generators name them differently
/// (<c>PetPagedResult</c> versus <c>PagedResultOfPet</c>). A name declared more than once is ambiguous and skipped.
/// Property names follow System.Text.Json: <c>[JsonPropertyName]</c> when present, otherwise the C# name compared
/// case-insensitively, which covers the camelCase web default. <c>[JsonIgnore]</c> properties are skipped.
/// </remarks>
internal sealed class TypeIndex
{
    private readonly Dictionary<string, List<INamedTypeSymbol>> _types = new(StringComparer.Ordinal);

    public void Add(SyntaxTree tree, SemanticModel model)
    {
        foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (declaration is InterfaceDeclarationSyntax
                || declaration is TypeDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 }
                || model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
            {
                continue;
            }

            if (!_types.TryGetValue(symbol.Name, out var list))
            {
                list = [];
                _types[symbol.Name] = list;
            }

            // Partial types have several declarations but are one symbol.
            if (!list.Contains(symbol, SymbolEqualityComparer.Default))
            {
                list.Add(symbol);
            }
        }
    }

    /// <summary>
    /// The deepest place along <paramref name="path"/> that exists in code: the property for <c>$.name</c>, or the type
    /// when the path ends at it or the property is gone (it was removed, so the type is where it used to be).
    /// </summary>
    public CodeLocation? Locate(string component, string path)
    {
        if (!_types.TryGetValue(component, out var candidates) || candidates.Count != 1)
        {
            return null;
        }

        ITypeSymbol current = candidates[0];
        var location = TypeLocation(candidates[0]);
        if (location is null)
        {
            return null;
        }

        foreach (var segment in Segments(path))
        {
            if (segment == "[]")
            {
                if (ElementType(current) is not { } element)
                {
                    break;
                }

                current = element;
                continue;
            }

            if (segment.StartsWith('<') || FindProperty(current, segment) is not { } property)
            {
                break; // variants, or a property that is not in the code (removed, or bound differently)
            }

            location = property.Locations.FirstOrDefault(l => l.IsInSource) is { } source
                ? CodeMap.ToCodeLocation(source)
                : location;
            current = Unwrap(property.Type);
        }

        return location;
    }

    private static CodeLocation? TypeLocation(INamedTypeSymbol type) =>
        type.Locations.FirstOrDefault(l => l.IsInSource) is { } location ? CodeMap.ToCodeLocation(location) : null;

    /// <summary><c>$.items[].name&lt;Cat&gt;</c> becomes <c>items</c>, <c>[]</c>, <c>name</c>, <c>&lt;Cat&gt;</c>.</summary>
    private static IEnumerable<string> Segments(string path)
    {
        var i = path.StartsWith('$') ? 1 : 0;
        while (i < path.Length)
        {
            switch (path[i])
            {
                case '.':
                    var end = path.IndexOfAny(['.', '[', '<'], i + 1);
                    end = end < 0 ? path.Length : end;
                    yield return path[(i + 1)..end];
                    i = end;
                    break;
                case '[':
                    yield return "[]";
                    i += 2;
                    break;
                case '<':
                    var close = path.IndexOf('>', i);
                    yield return close < 0 ? path[i..] : path[i..(close + 1)];
                    i = close < 0 ? path.Length : close + 1;
                    break;
                default:
                    yield break;
            }
        }
    }

    private static IPropertySymbol? FindProperty(ITypeSymbol type, string jsonName)
    {
        IPropertySymbol? byName = null;
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                    || HasAttribute(property, "JsonIgnore"))
                {
                    continue;
                }

                if (JsonPropertyName(property) is { } explicitName)
                {
                    if (string.Equals(explicitName, jsonName, StringComparison.Ordinal))
                    {
                        return property;
                    }

                    continue;
                }

                if (byName is null && string.Equals(property.Name, jsonName, StringComparison.OrdinalIgnoreCase))
                {
                    byName = property;
                }
            }
        }

        return byName;
    }

    private static string? JsonPropertyName(IPropertySymbol property) =>
        Attributes(property)
            .Where(a => EndpointScanner.Name(a) == "JsonPropertyName")
            .Select(a => a.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
            .OfType<LiteralExpressionSyntax>()
            .Select(l => l.Token.ValueText)
            .FirstOrDefault();

    private static bool HasAttribute(IPropertySymbol property, string name) =>
        Attributes(property).Any(a => EndpointScanner.Name(a) == name && a.ArgumentList is null);

    /// <summary>Attributes on the property declaration, or on the positional record parameter that declares it.</summary>
    private static IEnumerable<AttributeSyntax> Attributes(IPropertySymbol property) =>
        property.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .SelectMany(node => node switch
            {
                PropertyDeclarationSyntax p => p.AttributeLists,
                ParameterSyntax p => p.AttributeLists,
                _ => default(SyntaxList<AttributeListSyntax>),
            })
            .SelectMany(list => list.Attributes);

    /// <summary>Element type of an array or a single-argument generic collection.</summary>
    private static ITypeSymbol? ElementType(ITypeSymbol type) => Unwrap(type) switch
    {
        IArrayTypeSymbol array => Unwrap(array.ElementType),
        INamedTypeSymbol { TypeArguments.Length: 1 } generic when generic.SpecialType != SpecialType.System_String
            => Unwrap(generic.TypeArguments[0]),
        _ => null,
    };

    /// <summary>Removes <c>Nullable&lt;T&gt;</c> and nullable reference annotations.</summary>
    private static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
}
