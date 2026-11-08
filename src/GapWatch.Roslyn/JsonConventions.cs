using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GapWatch.Roslyn;

/// <summary>
/// The System.Text.Json settings that change the wire format: the property naming policy (camelCase, the ASP.NET Core
/// default) and whether enums are written as strings.
/// </summary>
/// <remarks>
/// Settings are found anywhere in the project, in the forms used inside <c>AddJsonOptions</c>,
/// <c>ConfigureHttpJsonOptions</c> or <c>Configure&lt;JsonOptions&gt;</c>: an assignment
/// <c>PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower</c> (or <c>null</c>), and
/// <c>Converters.Add(new JsonStringEnumConverter(...))</c>. The controller and minimal API options are not told apart.
/// </remarks>
/// <param name="PropertyNaming">The naming policy for properties; null keeps C# names.</param>
/// <param name="StringEnums">Whether a <c>JsonStringEnumConverter</c> is registered for every enum.</param>
/// <param name="EnumNaming">The naming policy passed to that converter; null keeps member names.</param>
internal sealed record JsonConventions(string? PropertyNaming, bool StringEnums, string? EnumNaming)
{
    public const string CamelCase = "CamelCase";

    public static JsonConventions Default { get; } = new(CamelCase, false, null);

    public static JsonConventions Find(Compilation compilation)
    {
        var conventions = Default;
        foreach (var tree in compilation.SyntaxTrees)
        {
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax { Left: var left, Right: var right }
                        when Identifier(left) == "PropertyNamingPolicy":
                        conventions = conventions with { PropertyNaming = Policy(right) };
                        break;

                    case BaseObjectCreationExpressionSyntax creation
                        when !creation.Ancestors().OfType<AttributeSyntax>().Any()
                             && CreatedType(creation) == "JsonStringEnumConverter":
                        var argument = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                        conventions = conventions with { StringEnums = true, EnumNaming = argument is null ? null : Policy(argument) };
                        break;
                }
            }
        }

        return conventions;
    }

    /// <summary><c>JsonNamingPolicy.CamelCase</c> becomes <c>CamelCase</c>; <c>null</c> stays null.</summary>
    private static string? Policy(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    private static string? Identifier(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    private static string? CreatedType(BaseObjectCreationExpressionSyntax creation) => creation switch
    {
        ObjectCreationExpressionSyntax { Type: var type } => type switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        },
        _ => null,
    };

    /// <summary>Applies a System.Text.Json naming policy by name. Unknown policies keep the name.</summary>
    public static string Apply(string? policy, string name) => policy switch
    {
        CamelCase => ToCamelCase(name),
        "SnakeCaseLower" => Separated(name, '_').ToLowerInvariant(),
        "SnakeCaseUpper" => Separated(name, '_').ToUpperInvariant(),
        "KebabCaseLower" => Separated(name, '-').ToLowerInvariant(),
        "KebabCaseUpper" => Separated(name, '-').ToUpperInvariant(),
        _ => name,
    };

    /// <summary>System.Text.Json's camelCase: leading capitals are lowered up to the start of the next word (<c>URLValue</c> is <c>urlValue</c>).</summary>
    private static string ToCamelCase(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
        {
            return name;
        }

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }

            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
            {
                break;
            }

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    /// <summary>Words split before a capital that follows a lower-case letter or digit, or that starts a word after an acronym.</summary>
    private static string Separated(string name, char separator)
    {
        var result = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c)
                && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                    || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                result.Append(separator);
            }

            result.Append(c);
        }

        return result.ToString();
    }
}
