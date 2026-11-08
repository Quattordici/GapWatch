using System.Globalization;
using GapWatch.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static GapWatch.Roslyn.SyntaxHelpers;

namespace GapWatch.Roslyn;

/// <summary>
/// Maps C# types to schemas the way <c>Microsoft.AspNetCore.OpenApi</c> documents them, with numbers as plain
/// <c>integer</c> and <c>number</c> (its <c>JsonNumberHandling.Strict</c> form).
/// </summary>
/// <remarks>
/// <para>
/// Classes, records and structs declared in the project become object schemas named after the type
/// (<c>PagedResult&lt;Pet&gt;</c> is <c>PagedResultOfPet</c>), so findings in shared schemas are grouped as for a document.
/// Their properties follow System.Text.Json: public instance properties with a getter, <c>[JsonPropertyName]</c> or
/// the naming policy, <c>[JsonIgnore]</c> skipped. A property is required with the <c>required</c> modifier,
/// <c>[Required]</c>, <c>[JsonRequired]</c>, or when it matches a parameter of the only public constructor (which
/// covers positional records).
/// </para>
/// <para>
/// Data annotations become the constraints the value-constraint rules compare. Enums are integers unless a
/// <c>JsonStringEnumConverter</c> applies. <c>[JsonPolymorphic]</c> and <c>[JsonDerivedType]</c> give variants with a
/// discriminator (<c>$type</c> by default). Types that do not resolve (ASP.NET Core and package types) are unspecified,
/// which compares as "anything" on both sides.
/// </para>
/// </remarks>
internal sealed class SchemaMapper(Compilation compilation, JsonConventions json)
{
    private const string DefaultDiscriminator = "$type";

    /// <summary>Types being expanded, to cut reference cycles.</summary>
    private readonly HashSet<ITypeSymbol> _expanding = new(SymbolEqualityComparer.Default);

    public SchemaMapper(Compilation compilation)
        : this(compilation, JsonConventions.Default)
    {
    }

    public SchemaContract Map(ITypeSymbol type) => Map(type, []);

    /// <summary>Maps a type and applies the annotations on the parameter or property that declares it.</summary>
    public SchemaContract Map(ITypeSymbol type, IReadOnlyList<AttributeSyntax> attributes)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            return Map(nullable.TypeArguments[0], attributes) with { Nullable = true };
        }

        var schema = Annotate(MapNonNullable(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), attributes), attributes);
        return type.IsReferenceType && type.NullableAnnotation is NullableAnnotation.Annotated or NullableAnnotation.None
            ? schema with { Nullable = true }
            : schema;
    }

    private SchemaContract MapNonNullable(ITypeSymbol type, IReadOnlyList<AttributeSyntax> attributes)
    {
        if (Primitive(type) is { } primitive)
        {
            return primitive;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return Enum((INamedTypeSymbol)type, attributes);
        }

        if (type is IArrayTypeSymbol array)
        {
            return Array(array.ElementType);
        }

        if (type is not INamedTypeSymbol named)
        {
            return SchemaContract.Unspecified;
        }

        if (IsDictionary(named))
        {
            return new SchemaContract { Types = ["object"] };
        }

        if (ElementType(named) is { } element)
        {
            return Array(element);
        }

        return IsDeclaredInSource(named) && named.TypeKind is TypeKind.Class or TypeKind.Struct
            ? Object(named)
            : SchemaContract.Unspecified;
    }

    private SchemaContract Array(ITypeSymbol element) => new() { Types = ["array"], Items = Map(element) };

    // --- Objects -------------------------------------------------------------------------------------------------

    private SchemaContract Object(INamedTypeSymbol type)
    {
        var name = SchemaName(type);
        var position = type.Locations.FirstOrDefault(l => l.IsInSource) is { } location ? Position(location) : null;
        if (!_expanding.Add(type))
        {
            return new SchemaContract { Types = ["object"], ReferenceName = name, IsRecursiveReference = true, Position = position };
        }

        try
        {
            var typeAttributes = TypeAttributes(type);
            var derived = typeAttributes.Named("JsonDerivedType").Select(DerivedType).OfType<(INamedTypeSymbol Type, string? Value)>().ToList();
            if (derived.Count > 0)
            {
                return Polymorphic(type, name, position, typeAttributes, derived);
            }

            return new SchemaContract
            {
                Types = ["object"],
                ReferenceName = name,
                Properties = Properties(type),
                Position = position,
            };
        }
        finally
        {
            _expanding.Remove(type);
        }
    }

    /// <summary>
    /// A base type with <c>[JsonDerivedType]</c>: one variant per derived type, each carrying the discriminator property,
    /// and a discriminator mapping from each value to the derived schema.
    /// </summary>
    private SchemaContract Polymorphic(
        INamedTypeSymbol type,
        string name,
        SourcePosition? position,
        IReadOnlyList<AttributeSyntax> typeAttributes,
        IReadOnlyList<(INamedTypeSymbol Type, string? Value)> derived)
    {
        var propertyName = typeAttributes.Named("JsonPolymorphic")
            .Select(a => StringProperty(a, "TypeDiscriminatorPropertyName", Model(a)))
            .FirstOrDefault(n => n is not null) ?? DefaultDiscriminator;

        var variants = new List<SchemaContract>();
        var mapping = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (subtype, value) in derived)
        {
            var variant = Map(subtype);
            if (value is not null && !variant.IsRecursiveReference)
            {
                var properties = new Dictionary<string, PropertyContract>(variant.Properties, StringComparer.Ordinal)
                {
                    [propertyName] = new PropertyContract(new SchemaContract { Types = ["string"], EnumValues = [value] }, true, false, false),
                };
                variant = variant with { Properties = properties };
            }

            variants.Add(variant);
            if (value is not null && variant.ReferenceName is { } target)
            {
                mapping[value] = target;
            }
        }

        return new SchemaContract
        {
            ReferenceName = name,
            Variants = variants,
            Discriminator = new Discriminator(propertyName, mapping),
            Position = position,
        };
    }

    /// <summary><c>[JsonDerivedType(typeof(Cat), "cat")]</c>, <c>[JsonDerivedType(typeof(Cat), typeDiscriminator: 1)]</c> or <c>[JsonDerivedType(typeof(Cat))]</c>.</summary>
    private (INamedTypeSymbol Type, string? Value)? DerivedType(AttributeSyntax attribute)
    {
        var model = Model(attribute);
        var arguments = Positional(attribute).Select(a => a.Expression).ToList();
        if (arguments.Count == 0 || TypeOf(arguments[0], model) is not INamedTypeSymbol type)
        {
            return null;
        }

        string? value = null;
        if (arguments.Count > 1)
        {
            var constant = model.GetConstantValue(arguments[1]);
            value = constant is { HasValue: true, Value: not null } ? Convert.ToString(constant.Value, CultureInfo.InvariantCulture) : null;
        }

        return (type, value);
    }

    private Dictionary<string, PropertyContract> Properties(INamedTypeSymbol type)
    {
        var constructorParameters = OnlyPublicConstructor(type)?.Parameters ?? [];
        var result = new Dictionary<string, PropertyContract>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                    || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }
                    || !seen.Add(property.Name))
                {
                    continue;
                }

                var attributes = PropertyAttributes(property);
                if (IsIgnored(attributes))
                {
                    continue;
                }

                var name = attributes.Named("JsonPropertyName").Select(a => FirstStringArgument(a, Model(a))).FirstOrDefault(n => n is not null)
                           ?? JsonConventions.Apply(json.PropertyNaming, property.Name);
                var required = property.IsRequired
                               || attributes.Has("Required") || attributes.Has("JsonRequired")
                               || constructorParameters.Any(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase)
                                                                 && SymbolEqualityComparer.Default.Equals(p.Type.WithNullableAnnotation(NullableAnnotation.None), property.Type.WithNullableAnnotation(NullableAnnotation.None)));

                result.TryAdd(name, new PropertyContract(Map(property.Type, attributes), required, ReadOnly: false, WriteOnly: false)
                {
                    Position = property.Locations.FirstOrDefault(l => l.IsInSource) is { } location ? Position(location) : null,
                });
            }
        }

        return result;
    }

    /// <summary><c>[JsonIgnore]</c>, or <c>[JsonIgnore(Condition = JsonIgnoreCondition.Always)]</c>; other conditions keep the property.</summary>
    private static bool IsIgnored(IReadOnlyList<AttributeSyntax> attributes) =>
        attributes.Named("JsonIgnore").Any(a => Property(a, "Condition") is not { } condition
                                                 || condition.ToString().EndsWith("Always", StringComparison.Ordinal));

    private static IMethodSymbol? OnlyPublicConstructor(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Struct)
        {
            return null; // a struct always has a parameterless constructor too
        }

        var constructors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
        return constructors is [{ Parameters.Length: > 0 } only] ? only : null;
    }

    /// <summary>The schema name generators use: <c>Pet</c>, or <c>PagedResultOfPet</c> for a generic type.</summary>
    private static string SchemaName(INamedTypeSymbol type) =>
        type.TypeArguments.Length == 0
            ? type.Name
            : type.Name + "Of" + string.Join("And", type.TypeArguments.Select(t => t is INamedTypeSymbol n ? SchemaName(n) : t.Name));

    // --- Enums ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Integers, as System.Text.Json writes enums by default; strings with a <c>JsonStringEnumConverter</c> on the enum,
    /// on the property, or registered for the whole app.
    /// </summary>
    private SchemaContract Enum(INamedTypeSymbol type, IReadOnlyList<AttributeSyntax> attributes)
    {
        var members = type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
        var converter = HasStringEnumConverter(attributes) || HasStringEnumConverter(TypeAttributes(type));
        if (!converter && !json.StringEnums)
        {
            return new SchemaContract
            {
                Types = ["integer"],
                EnumValues = members
                    .Select(f => Convert.ToString(f.ConstantValue, CultureInfo.InvariantCulture) ?? string.Empty)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList(),
            };
        }

        // A converter in an attribute cannot be given a naming policy; one registered in the options can.
        var policy = converter ? null : json.EnumNaming;
        return new SchemaContract
        {
            Types = ["string"],
            EnumValues = members
                .Select(f => FieldAttributes(f).Named("JsonStringEnumMemberName").Select(a => FirstStringArgument(a, Model(a))).FirstOrDefault(n => n is not null)
                             ?? JsonConventions.Apply(policy, f.Name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
        };
    }

    /// <summary><c>[JsonConverter(typeof(JsonStringEnumConverter))]</c>, or its generic form.</summary>
    private static bool HasStringEnumConverter(IEnumerable<AttributeSyntax> attributes) =>
        attributes.Named("JsonConverter").Any(a => Positional(a).FirstOrDefault()?.Expression is TypeOfExpressionSyntax { Type: var type }
                                                   && type.ToString().Contains("JsonStringEnumConverter", StringComparison.Ordinal));

    // --- Annotations ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Data annotations as <c>Microsoft.AspNetCore.OpenApi</c> applies them: <c>[Range]</c> (with
    /// <c>MinimumIsExclusive</c> and <c>MaximumIsExclusive</c>), <c>[MinLength]</c>, <c>[MaxLength]</c>, <c>[Length]</c>
    /// and <c>[StringLength]</c> (to strings, or item counts for arrays), and <c>[RegularExpression]</c>.
    /// </summary>
    private SchemaContract Annotate(SchemaContract schema, IReadOnlyList<AttributeSyntax> attributes)
    {
        var isArray = schema.Types.Contains("array");
        foreach (var attribute in attributes)
        {
            var model = Model(attribute);
            var arguments = Positional(attribute).Select(a => a.Expression).ToList();
            double? Number(int index) => index < arguments.Count ? NumberValue(arguments[index], model) : null;

            switch (Name(attribute))
            {
                case "Range" when arguments.Count >= 2 && arguments[0] is not TypeOfExpressionSyntax:
                    schema = schema with
                    {
                        Minimum = Number(0) is { } lower ? new Bound(lower, BoolProperty(attribute, "MinimumIsExclusive", model)) : schema.Minimum,
                        Maximum = Number(1) is { } upper ? new Bound(upper, BoolProperty(attribute, "MaximumIsExclusive", model)) : schema.Maximum,
                    };
                    break;
                case "MinLength" when Number(0) is { } min:
                    schema = isArray ? schema with { MinItems = new Bound(min) } : schema with { MinLength = new Bound(min) };
                    break;
                case "MaxLength" when Number(0) is { } max:
                    schema = isArray ? schema with { MaxItems = new Bound(max) } : schema with { MaxLength = new Bound(max) };
                    break;
                case "Length" when Number(0) is { } min && Number(1) is { } max:
                    schema = isArray
                        ? schema with { MinItems = new Bound(min), MaxItems = new Bound(max) }
                        : schema with { MinLength = new Bound(min), MaxLength = new Bound(max) };
                    break;
                case "StringLength" when Number(0) is { } max:
                    var minimumLength = Property(attribute, "MinimumLength") is { } m ? NumberValue(m, model) : null;
                    schema = schema with
                    {
                        MaxLength = new Bound(max),
                        MinLength = minimumLength is { } shortest and > 0 ? new Bound(shortest) : schema.MinLength,
                    };
                    break;
                case "RegularExpression" when arguments.Count > 0 && ConstantString(arguments[0], model) is { } pattern:
                    schema = schema with { Pattern = pattern };
                    break;
            }
        }

        return schema;
    }

    private static double? NumberValue(ExpressionSyntax expression, SemanticModel model)
    {
        var constant = model.GetConstantValue(expression);
        return constant is { HasValue: true, Value: not null and not string and not bool }
            ? Convert.ToDouble(constant.Value, CultureInfo.InvariantCulture)
            : null;
    }

    private static bool BoolProperty(AttributeSyntax attribute, string name, SemanticModel model) =>
        Property(attribute, name) is { } expression && model.GetConstantValue(expression) is { HasValue: true, Value: true };

    // --- Attributes ----------------------------------------------------------------------------------------------

    private SemanticModel Model(SyntaxNode node) => compilation.GetSemanticModel(node.SyntaxTree);

    private static List<AttributeSyntax> TypeAttributes(ITypeSymbol type) =>
        type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<BaseTypeDeclarationSyntax>()
            .SelectMany(d => Attributes(d.AttributeLists))
            .ToList();

    /// <summary>Attributes on the property, or on the positional record parameter that declares it.</summary>
    private static List<AttributeSyntax> PropertyAttributes(IPropertySymbol property) =>
        property.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .SelectMany(node => node switch
            {
                PropertyDeclarationSyntax p => Attributes(p.AttributeLists),
                ParameterSyntax p => Attributes(p.AttributeLists),
                _ => [],
            })
            .ToList();

    private static List<AttributeSyntax> FieldAttributes(IFieldSymbol field) =>
        field.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<EnumMemberDeclarationSyntax>()
            .SelectMany(m => Attributes(m.AttributeLists))
            .ToList();

    // --- Type classification -------------------------------------------------------------------------------------

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
