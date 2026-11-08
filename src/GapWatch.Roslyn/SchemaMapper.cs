using GapWatch.Core.Model;
using Microsoft.CodeAnalysis;

namespace GapWatch.Roslyn;

/// <summary>
/// Maps C# types to schemas the way <c>Microsoft.AspNetCore.OpenApi</c> documents them, with numbers as plain
/// <c>integer</c> and <c>number</c> (its <c>JsonNumberHandling.Strict</c> form).
/// </summary>
/// <remarks>
/// Types declared in the project become object schemas named after the type, so findings in shared schemas are grouped
/// the same way as for a document. Types that do not resolve (ASP.NET Core and package types) are unspecified, which
/// compares as "anything" on both sides.
/// </remarks>
internal sealed class SchemaMapper
{
    public SchemaContract Map(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            return Map(nullable.TypeArguments[0]) with { Nullable = true };
        }

        var schema = MapNonNullable(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
        return type.NullableAnnotation == NullableAnnotation.Annotated && type.IsReferenceType ? schema with { Nullable = true } : schema;
    }

    private SchemaContract MapNonNullable(ITypeSymbol type)
    {
        if (Primitive(type) is { } primitive)
        {
            return primitive;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return Enum((INamedTypeSymbol)type);
        }

        if (type is IArrayTypeSymbol array)
        {
            return Array(array.ElementType);
        }

        if (type is INamedTypeSymbol named)
        {
            if (IsDictionary(named))
            {
                return new SchemaContract { Types = ["object"] };
            }

            if (ElementType(named) is { } element)
            {
                return Array(element);
            }

            if (IsDeclaredInSource(named) && named.TypeKind is TypeKind.Class or TypeKind.Struct)
            {
                return new SchemaContract
                {
                    Types = ["object"],
                    ReferenceName = named.Name,
                    Position = named.Locations.FirstOrDefault(l => l.IsInSource) is { } location ? SyntaxHelpers.Position(location) : null,
                };
            }
        }

        return SchemaContract.Unspecified;
    }

    private SchemaContract Array(ITypeSymbol element) => new() { Types = ["array"], Items = Map(element) };

    /// <summary>Enums are integers, as System.Text.Json writes them by default.</summary>
    private static SchemaContract Enum(INamedTypeSymbol type) => new()
    {
        Types = ["integer"],
        EnumValues = type.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .Select(f => Convert.ToString(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList(),
    };

    /// <summary>The table in "Include OpenAPI metadata for data types" (Microsoft Learn, ASP.NET Core 10).</summary>
    public static SchemaContract? Primitive(ITypeSymbol type)
    {
        (string Type, string? Format)? mapping = type.SpecialType switch
        {
            SpecialType.System_Boolean => ("boolean", null),
            SpecialType.System_Byte => ("integer", "uint8"),
            SpecialType.System_SByte => ("integer", "int8"),
            SpecialType.System_Int16 => ("integer", "int16"),
            SpecialType.System_UInt16 => ("integer", "uint16"),
            SpecialType.System_Int32 => ("integer", "int32"),
            SpecialType.System_UInt32 => ("integer", "uint32"),
            SpecialType.System_Int64 => ("integer", "int64"),
            SpecialType.System_UInt64 => ("integer", "uint64"),
            SpecialType.System_Single => ("number", "float"),
            SpecialType.System_Double => ("number", "double"),
            SpecialType.System_Decimal => ("number", "double"),
            SpecialType.System_Char => ("string", "char"),
            SpecialType.System_String => ("string", null),
            SpecialType.System_DateTime => ("string", "date-time"),
            _ => FullName(type) switch
            {
                "System.DateTimeOffset" => ("string", "date-time"),
                "System.DateOnly" => ("string", "date"),
                "System.TimeOnly" => ("string", "time"),
                "System.TimeSpan" => ("string", null),
                "System.Guid" => ("string", "uuid"),
                "System.Uri" => ("string", "uri"),
                _ => type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } ? ("string", "byte") : null,
            },
        };

        if (mapping is not { } found)
        {
            return type.SpecialType == SpecialType.System_Object ? SchemaContract.Unspecified : null;
        }

        return found.Type == "string" && found.Format == "char"
            ? new SchemaContract { Types = [found.Type], Format = found.Format, MinLength = new Bound(1), MaxLength = new Bound(1) }
            : new SchemaContract { Types = [found.Type], Format = found.Format };
    }

    /// <summary>Whether a value of this type is bound from a single string (route, query, header): primitives, enums and their nullable forms.</summary>
    public static bool IsSimple(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        return type.TypeKind == TypeKind.Enum
               || (Primitive(type) is { } schema && schema != SchemaContract.Unspecified && type is not IArrayTypeSymbol);
    }

    /// <summary>Element type of an array or a collection of simple values, which binds from repeated query values.</summary>
    public static ITypeSymbol? SimpleElementType(ITypeSymbol type) =>
        (type is IArrayTypeSymbol array ? array.ElementType : type is INamedTypeSymbol named ? ElementType(named) : null) is { } element
        && IsSimple(element)
            ? element
            : null;

    public static bool IsDeclaredInSource(ITypeSymbol type) =>
        type.TypeKind != TypeKind.Error && type.Locations.Any(l => l.IsInSource && l.SourceTree?.FilePath != SourceCompilation.ImplicitUsings);

    /// <summary>The element type of a collection (<c>List&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>, ...); null for strings and dictionaries.</summary>
    public static ITypeSymbol? ElementType(INamedTypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String || IsDictionary(type))
        {
            return null;
        }

        if (type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
            || (type is { Name: "IAsyncEnumerable", Arity: 1 } && FullName(type) == "System.Collections.Generic.IAsyncEnumerable"))
        {
            return type.TypeArguments[0];
        }

        return type.AllInterfaces
            .FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            ?.TypeArguments[0];
    }

    private static bool IsDictionary(INamedTypeSymbol type) =>
        IsDictionaryInterface(type) || type.AllInterfaces.Any(IsDictionaryInterface);

    private static bool IsDictionaryInterface(INamedTypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";

    private static string FullName(ITypeSymbol type) => $"{type.ContainingNamespace?.ToDisplayString()}.{type.Name}";
}
