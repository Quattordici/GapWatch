using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static GapWatch.Roslyn.SyntaxHelpers;

namespace GapWatch.Roslyn;

/// <summary>An endpoint found in code, with the syntax the contract reader needs to describe it.</summary>
/// <param name="Method">Upper-case HTTP method.</param>
/// <param name="Route">Route template as declared, with constraints, e.g. <c>/api/pets/{id:int}</c>.</param>
/// <param name="Location">The action method name or the <c>Map*</c> call.</param>
/// <param name="Parameters">The action or handler parameters, in order.</param>
/// <param name="Handler">The action method, or the minimal API handler (a lambda's symbol or the named method).</param>
/// <param name="Lambda">The handler when it is a lambda, whose return type may have to be inferred from its body.</param>
/// <param name="Attributes">Attributes on the action or handler.</param>
/// <param name="TypeAttributes">Attributes on the controller and its base classes, closest first (empty for minimal APIs).</param>
/// <param name="Conventions">
/// Fluent calls that configure a minimal API endpoint: on the <c>Map*</c> call and on the groups it belongs to, such as
/// <c>RequireAuthorization()</c> or <c>Produces&lt;T&gt;(200)</c>.
/// </param>
/// <param name="IsController">Whether this is a controller action.</param>
/// <param name="IsApiController">Whether the controller has <c>[ApiController]</c>, which changes binding inference.</param>
internal sealed record EndpointDeclaration(
    string Method,
    string Route,
    Location Location,
    IReadOnlyList<ParameterSyntax> Parameters,
    IMethodSymbol? Handler,
    AnonymousFunctionExpressionSyntax? Lambda,
    IReadOnlyList<AttributeSyntax> Attributes,
    IReadOnlyList<AttributeSyntax> TypeAttributes,
    IReadOnlyList<InvocationExpressionSyntax> Conventions,
    bool IsController,
    bool IsApiController);

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

    public static IEnumerable<EndpointDeclaration> Scan(SyntaxTree tree, Compilation compilation)
    {
        var model = compilation.GetSemanticModel(tree);
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
            if (MinimalApi(invocation, model, compilation) is { } endpoint)
            {
                yield return endpoint;
            }
        }
    }

    // --- Controllers ---------------------------------------------------------------------------------------------

    private static IEnumerable<EndpointDeclaration> Controller(ClassDeclarationSyntax type, SemanticModel model)
    {
        if (type.Modifiers.Any(SyntaxKind.AbstractKeyword) || model.GetDeclaredSymbol(type) is not { } symbol)
        {
            yield break;
        }

        var hierarchy = Hierarchy(symbol).ToList();
        var typeAttributes = hierarchy.SelectMany(c => Attributes(c.AttributeLists)).ToList();
        var isController = !Attributes(type.AttributeLists).Has("NonController")
                           && (symbol.Name.EndsWith("Controller", StringComparison.Ordinal)
                               || typeAttributes.Has("ApiController") || typeAttributes.Has("Controller")
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
            var attributes = Attributes(method.AttributeLists).ToList();
            if (!method.Modifiers.Any(SyntaxKind.PublicKeyword)
                || method.Modifiers.Any(SyntaxKind.StaticKeyword)
                || attributes.Has("NonAction"))
            {
                continue;
            }

            var action = Templates(method.AttributeLists, "ActionName", model).FirstOrDefault()
                         ?? TrimAsync(method.Identifier.ValueText);
            var methodRoutes = Templates(method.AttributeLists, "Route", model).ToList();

            foreach (var (verb, template) in HttpVerbs(attributes, model))
            {
                List<string?> templates = template is not null ? [template] : methodRoutes.Count > 0 ? [.. methodRoutes] : [null];
                foreach (var prefix in prefixes)
                {
                    foreach (var actionTemplate in templates)
                    {
                        var route = RouteTemplate.ReplaceTokens(RouteTemplate.Combine(prefix, actionTemplate), controller, action, area);
                        yield return new EndpointDeclaration(
                            verb,
                            route,
                            method.Identifier.GetLocation(),
                            method.ParameterList.Parameters,
                            model.GetDeclaredSymbol(method),
                            null,
                            attributes,
                            typeAttributes,
                            [],
                            IsController: true,
                            IsApiController: typeAttributes.Has("ApiController"));
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

    private static IEnumerable<(string Verb, string? Template)> HttpVerbs(IEnumerable<AttributeSyntax> attributes, SemanticModel model)
    {
        foreach (var attribute in attributes)
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

    private static EndpointDeclaration? MinimalApi(InvocationExpressionSyntax invocation, SemanticModel model, Compilation compilation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || !MapMethods.TryGetValue(member.Name.Identifier.ValueText, out var verb)
            || invocation.ArgumentList.Arguments.Count < 2
            || ConstantString(invocation.ArgumentList.Arguments[0].Expression, model) is not { } pattern)
        {
            return null;
        }

        var groups = new List<InvocationExpressionSyntax>();
        Receivers(member.Expression, model, groups, depth: 0);
        var prefix = groups
            .Where(g => MethodName(g) == "MapGroup" && g.ArgumentList.Arguments.Count > 0)
            .Select(g => ConstantString(g.ArgumentList.Arguments[0].Expression, model))
            .Aggregate((string?)null, (outer, group) => group is null ? outer : RouteTemplate.Append(outer, group));

        // Calls chained after the Map call: app.MapGet(...).RequireAuthorization().WithName("...").
        var conventions = new List<InvocationExpressionSyntax>(groups.Where(g => MethodName(g) != "MapGroup"));
        for (SyntaxNode node = invocation;
             node.Parent is MemberAccessExpressionSyntax access && access.Expression == node && access.Parent is InvocationExpressionSyntax call;
             node = call)
        {
            conventions.Add(call);
        }

        var handler = invocation.ArgumentList.Arguments[^1].Expression;
        var (parameters, symbol, lambda, attributes) = Handler(handler, model, compilation);
        return new EndpointDeclaration(
            verb,
            RouteTemplate.Append(prefix, pattern),
            member.Name.GetLocation(),
            parameters,
            symbol,
            lambda,
            attributes,
            [],
            conventions,
            IsController: false,
            IsApiController: false);
    }

    /// <summary>
    /// Collects the calls on the receiver of a <c>Map*</c> call, outermost (the app) first: <c>MapGroup</c> calls and
    /// fluent calls such as <c>WithTags</c>, chained directly or made on a local variable or field that holds the group.
    /// </summary>
    private static void Receivers(ExpressionSyntax receiver, SemanticModel model, List<InvocationExpressionSyntax> calls, int depth)
    {
        if (depth > 16)
        {
            return;
        }

        switch (receiver)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } call:
                Receivers(inner.Expression, model, calls, depth + 1);
                calls.Add(call);
                return;

            case IdentifierNameSyntax identifier:
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (symbol is not (ILocalSymbol or IFieldSymbol))
                {
                    return;
                }

                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
                        && initializer.SyntaxTree == identifier.SyntaxTree)
                    {
                        Receivers(initializer, model, calls, depth + 1);
                    }
                }

                // Statements such as `pets.RequireAuthorization();` configure every endpoint of the group.
                calls.AddRange(identifier.SyntaxTree.GetRoot().DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => call.Parent is ExpressionStatementSyntax
                                   && call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax target }
                                   && target.Identifier.ValueText == identifier.Identifier.ValueText
                                   && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(target).Symbol, symbol)
                                   && MethodName(call) is { } name && !name.StartsWith("Map", StringComparison.Ordinal)));
                return;

            case ParenthesizedExpressionSyntax parenthesized:
                Receivers(parenthesized.Expression, model, calls, depth + 1);
                return;
        }
    }

    private static (IReadOnlyList<ParameterSyntax> Parameters, IMethodSymbol? Symbol, AnonymousFunctionExpressionSyntax? Lambda, IReadOnlyList<AttributeSyntax> Attributes)
        Handler(ExpressionSyntax handler, SemanticModel model, Compilation compilation)
    {
        switch (handler)
        {
            case ParenthesizedLambdaExpressionSyntax lambda:
                return (lambda.ParameterList.Parameters, model.GetSymbolInfo(lambda).Symbol as IMethodSymbol, lambda, Attributes(lambda.AttributeLists).ToList());
            case SimpleLambdaExpressionSyntax simple:
                return ([simple.Parameter], model.GetSymbolInfo(simple).Symbol as IMethodSymbol, simple, Attributes(simple.AttributeLists).ToList());
        }

        // A method group: find the declaration of the method it names.
        var info = model.GetSymbolInfo(handler);
        var method = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        var declaration = method?.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();
        return declaration switch
        {
            MethodDeclarationSyntax m => (m.ParameterList.Parameters, method, null, Attributes(m.AttributeLists).ToList()),
            LocalFunctionStatementSyntax f => (f.ParameterList.Parameters, method, null, Attributes(f.AttributeLists).ToList()),
            _ => ([], method, null, []),
        };
    }

    // --- Shared helpers ------------------------------------------------------------------------------------------

    /// <summary>String arguments of every attribute with the given name, e.g. all <c>[Route("...")]</c> templates.</summary>
    private static IEnumerable<string> Templates(SyntaxList<AttributeListSyntax> lists, string attribute, SemanticModel model) =>
        Attributes(lists)
            .Named(attribute)
            .Select(a => FirstStringArgument(a, model))
            .OfType<string>();
}
