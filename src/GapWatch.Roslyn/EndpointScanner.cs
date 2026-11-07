using GapWatch.Core.Findings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GapWatch.Roslyn;

/// <summary>Finds attribute-routed controller actions and minimal API endpoints in one syntax tree.</summary>
internal static class EndpointScanner
{
    private static readonly Dictionary<string, string> HttpAttributes = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH",
        ["HttpHead"] = "HEAD",
        ["HttpOptions"] = "OPTIONS",
    };

    private static readonly Dictionary<string, string> MapMethods = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
    };

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

    public static IEnumerable<Endpoint> Scan(SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();

        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            foreach (var endpoint in Controller(type, model))
            {
                yield return endpoint;
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (MinimalApi(invocation, model) is { } endpoint)
            {
                yield return endpoint;
            }
        }
    }

    // --- Controllers ---------------------------------------------------------------------------------------------

    private static IEnumerable<Endpoint> Controller(ClassDeclarationSyntax type, SemanticModel model)
    {
        if (type.Modifiers.Any(SyntaxKind.AbstractKeyword) || model.GetDeclaredSymbol(type) is not { } symbol)
        {
            yield break;
        }

        var hierarchy = Hierarchy(symbol).ToList();
        var isController = !HasAttribute(type, "NonController")
                           && (symbol.Name.EndsWith("Controller", StringComparison.Ordinal)
                               || hierarchy.Any(c => HasAttribute(c, "ApiController") || HasAttribute(c, "Controller"))
                               || type.BaseList?.Types.Any(t => t.Type.ToString().Contains("Controller", StringComparison.Ordinal)) == true);
        if (!isController)
        {
            yield break;
        }

        // Route on the class, or inherited from the closest base class that declares one.
        List<string?> prefixes = hierarchy
            .Select(c => Templates(c.AttributeLists, "Route", model).ToList<string?>())
            .FirstOrDefault(t => t.Count > 0) ?? [null];

        var controller = symbol.Name.EndsWith("Controller", StringComparison.Ordinal) && symbol.Name.Length > "Controller".Length
            ? symbol.Name[..^"Controller".Length]
            : symbol.Name;
        var area = hierarchy.SelectMany(c => Templates(c.AttributeLists, "Area", model)).FirstOrDefault();

        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.PublicKeyword)
                || method.Modifiers.Any(SyntaxKind.StaticKeyword)
                || HasAttribute(method.AttributeLists, "NonAction"))
            {
                continue;
            }

            var action = Templates(method.AttributeLists, "ActionName", model).FirstOrDefault()
                         ?? TrimAsync(method.Identifier.ValueText);
            var methodRoutes = Templates(method.AttributeLists, "Route", model).ToList();
            var location = CodeMap.ToCodeLocation(method.Identifier.GetLocation());
            var parameters = Parameters(method.ParameterList);

            foreach (var (verb, template) in HttpVerbs(method.AttributeLists, model))
            {
                List<string?> templates = template is not null ? [template] : methodRoutes.Count > 0 ? [.. methodRoutes] : [null];
                foreach (var prefix in prefixes)
                {
                    foreach (var actionTemplate in templates)
                    {
                        var route = RouteTemplate.ReplaceTokens(RouteTemplate.Combine(prefix, actionTemplate), controller, action, area);
                        yield return new Endpoint(verb, route, location, parameters);
                    }
                }
            }
        }
    }

    /// <summary>The class and its base classes declared in source, closest first; each with its declaration syntax.</summary>
    private static IEnumerable<ClassDeclarationSyntax> Hierarchy(INamedTypeSymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.BaseType)
        {
            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is ClassDeclarationSyntax declaration)
                {
                    yield return declaration;
                }
            }
        }
    }

    private static IEnumerable<(string Verb, string? Template)> HttpVerbs(SyntaxList<AttributeListSyntax> lists, SemanticModel model)
    {
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            if (HttpAttributes.TryGetValue(Name(attribute), out var verb))
            {
                yield return (verb, FirstStringArgument(attribute, model));
            }
        }
    }

    private static string TrimAsync(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length ? name[..^"Async".Length] : name;

    // --- Minimal APIs --------------------------------------------------------------------------------------------

    private static Endpoint? MinimalApi(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || !MapMethods.TryGetValue(member.Name.Identifier.ValueText, out var verb)
            || invocation.ArgumentList.Arguments.Count < 2
            || ConstantString(invocation.ArgumentList.Arguments[0].Expression, model) is not { } pattern)
        {
            return null;
        }

        var route = RouteTemplate.Append(GroupPrefix(member.Expression, model, depth: 0), pattern);
        var handler = invocation.ArgumentList.Arguments[^1].Expression;
        return new Endpoint(
            verb,
            route,
            CodeMap.ToCodeLocation(member.Name.GetLocation()),
            HandlerParameters(handler, model));
    }

    /// <summary>
    /// The route prefix contributed by <c>MapGroup</c> calls on the receiver: chained directly, through fluent calls
    /// such as <c>WithTags</c>, or through a local variable or field that holds the group.
    /// </summary>
    private static string? GroupPrefix(ExpressionSyntax receiver, SemanticModel model, int depth)
    {
        if (depth > 16)
        {
            return null;
        }

        switch (receiver)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } call:
                var outer = GroupPrefix(inner.Expression, model, depth + 1);
                return inner.Name.Identifier.ValueText == "MapGroup"
                       && call.ArgumentList.Arguments.Count > 0
                       && ConstantString(call.ArgumentList.Arguments[0].Expression, model) is { } group
                    ? RouteTemplate.Append(outer, group)
                    : outer;

            case IdentifierNameSyntax identifier:
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (symbol is ILocalSymbol or IFieldSymbol)
                {
                    foreach (var reference in symbol.DeclaringSyntaxReferences)
                    {
                        if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
                            && initializer.SyntaxTree == identifier.SyntaxTree)
                        {
                            return GroupPrefix(initializer, model, depth + 1);
                        }
                    }
                }

                return null;

            case ParenthesizedExpressionSyntax parenthesized:
                return GroupPrefix(parenthesized.Expression, model, depth + 1);

            default:
                return null;
        }
    }

    private static IReadOnlyDictionary<string, CodeLocation> HandlerParameters(ExpressionSyntax handler, SemanticModel model)
    {
        switch (handler)
        {
            case ParenthesizedLambdaExpressionSyntax lambda:
                return Parameters(lambda.ParameterList);
            case SimpleLambdaExpressionSyntax simple:
                return Parameters(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(simple.Parameter)));
        }

        // A method group: find the declaration of the method it names.
        var info = model.GetSymbolInfo(handler);
        var method = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        var declaration = method?.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();
        return declaration switch
        {
            MethodDeclarationSyntax m => Parameters(m.ParameterList),
            LocalFunctionStatementSyntax f => Parameters(f.ParameterList),
            _ => new Dictionary<string, CodeLocation>(),
        };
    }

    // --- Shared helpers ------------------------------------------------------------------------------------------

    private static Dictionary<string, CodeLocation> Parameters(ParameterListSyntax list)
    {
        var result = new Dictionary<string, CodeLocation>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in list.Parameters)
        {
            if (HasAnyAttribute(parameter.AttributeLists, NonParameterAttributes)
                || parameter.Type?.ToString() is "CancellationToken" or "HttpContext" or "HttpRequest" or "HttpResponse")
            {
                continue;
            }

            var name = parameter.AttributeLists
                .SelectMany(l => l.Attributes)
                .Where(a => BindingAttributes.Contains(Name(a)))
                .Select(NamedStringArgument)
                .FirstOrDefault(n => n is not null)
                ?? parameter.Identifier.ValueText;

            result.TryAdd(name, CodeMap.ToCodeLocation(parameter.Identifier.GetLocation()));
        }

        return result;
    }

    /// <summary>String arguments of every attribute with the given name, e.g. all <c>[Route("...")]</c> templates.</summary>
    private static IEnumerable<string> Templates(SyntaxList<AttributeListSyntax> lists, string attribute, SemanticModel model) =>
        lists.SelectMany(l => l.Attributes)
            .Where(a => Name(a) == attribute)
            .Select(a => FirstStringArgument(a, model))
            .OfType<string>();

    private static bool HasAttribute(ClassDeclarationSyntax type, string attribute) => HasAttribute(type.AttributeLists, attribute);

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string attribute) =>
        lists.SelectMany(l => l.Attributes).Any(a => Name(a) == attribute);

    private static bool HasAnyAttribute(SyntaxList<AttributeListSyntax> lists, HashSet<string> attributes) =>
        lists.SelectMany(l => l.Attributes).Any(a => attributes.Contains(Name(a)));

    /// <summary>Attribute name without namespace and without the <c>Attribute</c> suffix.</summary>
    internal static string Name(AttributeSyntax attribute)
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

    /// <summary>The first positional argument (or <c>template:</c>) as a constant string.</summary>
    private static string? FirstStringArgument(AttributeSyntax attribute, SemanticModel model) =>
        attribute.ArgumentList?.Arguments
            .Where(a => a.NameEquals is null && (a.NameColon is null || a.NameColon.Name.Identifier.ValueText is "template" or "name"))
            .Select(a => ConstantString(a.Expression, model))
            .FirstOrDefault();

    /// <summary>The <c>Name = "..."</c> argument of a binding attribute.</summary>
    private static string? NamedStringArgument(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments
            .Where(a => a.NameEquals?.Name.Identifier.ValueText == "Name")
            .Select(a => a.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
                ? literal.Token.ValueText
                : null)
            .FirstOrDefault();

    internal static string? ConstantString(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
        {
            return literal.Token.ValueText;
        }

        var constant = model.GetConstantValue(expression);
        return constant is { HasValue: true, Value: string value } ? value : null;
    }
}
