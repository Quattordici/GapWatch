using System.Text.RegularExpressions;
using GapWatch.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GapWatch.Roslyn;

/// <summary>
/// Reads attributes and arguments by name and from syntax, because ASP.NET Core is not referenced and its types do not
/// resolve.
/// </summary>
internal static partial class SyntaxHelpers
{
    /// <summary>Attribute name without namespace, type arguments and the <c>Attribute</c> suffix.</summary>
    public static string Name(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    public static IEnumerable<AttributeSyntax> Attributes(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes);

    public static IEnumerable<AttributeSyntax> Named(this IEnumerable<AttributeSyntax> attributes, string name) =>
        attributes.Where(a => Name(a) == name);

    public static bool Has(this IEnumerable<AttributeSyntax> attributes, string name) => attributes.Any(a => Name(a) == name);

    /// <summary>Positional arguments (no <c>Name = ...</c>), in order.</summary>
    public static IReadOnlyList<AttributeArgumentSyntax> Positional(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Where(a => a.NameEquals is null).ToList() ?? [];

    /// <summary>The <c>Name = value</c> argument of an attribute.</summary>
    public static ExpressionSyntax? Property(AttributeSyntax attribute, string name) =>
        attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name)?.Expression;

    /// <summary>The first positional argument (or <c>template:</c>) as a constant string.</summary>
    public static string? FirstStringArgument(AttributeSyntax attribute, SemanticModel model) =>
        attribute.ArgumentList?.Arguments
            .Where(a => a.NameEquals is null && (a.NameColon is null || a.NameColon.Name.Identifier.ValueText is "template" or "name"))
            .Select(a => ConstantString(a.Expression, model))
            .FirstOrDefault();

    /// <summary>The <c>Name = "..."</c> argument of an attribute, as a constant string.</summary>
    public static string? StringProperty(AttributeSyntax attribute, string name, SemanticModel model) =>
        Property(attribute, name) is { } expression ? ConstantString(expression, model) : null;

    public static string? ConstantString(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
        {
            return literal.Token.ValueText;
        }

        var constant = model.GetConstantValue(expression);
        return constant is { HasValue: true, Value: string value } ? value : null;
    }

    /// <summary>
    /// A status code: a constant (<c>200</c>, <c>(int)HttpStatusCode.OK</c>, a project constant), or a member named like
    /// ASP.NET Core's <c>StatusCodes.Status404NotFound</c>, which does not resolve here.
    /// </summary>
    public static int? StatusCode(ExpressionSyntax expression, SemanticModel model)
    {
        var constant = model.GetConstantValue(expression);
        if (constant is { HasValue: true, Value: not null and not string and not bool }
            && Convert.ToInt64(constant.Value, System.Globalization.CultureInfo.InvariantCulture) is var number and >= 100 and <= 599)
        {
            return (int)number;
        }

        switch (expression)
        {
            case CastExpressionSyntax cast:
                return StatusCode(cast.Expression, model);
            case ParenthesizedExpressionSyntax parenthesized:
                return StatusCode(parenthesized.Expression, model);
        }

        var name = expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
        return name is not null && StatusCodeName().Match(name) is { Success: true } match
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The type in <c>typeof(T)</c>.</summary>
    public static ITypeSymbol? TypeOf(ExpressionSyntax expression, SemanticModel model) =>
        expression is TypeOfExpressionSyntax typeOf ? model.GetTypeInfo(typeOf.Type).Type : null;

    /// <summary>The type arguments of a generic attribute such as <c>[ProducesResponseType&lt;Pet&gt;(200)]</c>.</summary>
    public static IReadOnlyList<TypeSyntax> TypeArguments(AttributeSyntax attribute) => attribute.Name switch
    {
        GenericNameSyntax generic => generic.TypeArgumentList.Arguments,
        QualifiedNameSyntax { Right: GenericNameSyntax generic } => generic.TypeArgumentList.Arguments,
        _ => [],
    };

    /// <summary>The method name of an invocation such as <c>x.RequireAuthorization()</c> or <c>x.Produces&lt;T&gt;()</c>.</summary>
    public static string? MethodName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The type arguments of a generic method call such as <c>Produces&lt;Pet&gt;(200)</c>.</summary>
    public static IReadOnlyList<TypeSyntax> TypeArguments(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic.TypeArgumentList.Arguments,
        GenericNameSyntax generic => generic.TypeArgumentList.Arguments,
        _ => [],
    };

    public static SourcePosition Position(Location location)
    {
        var span = location.GetLineSpan();
        return new SourcePosition(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1) { File = span.Path };
    }

    [GeneratedRegex(@"^Status(\d{3})")]
    private static partial Regex StatusCodeName();
}
