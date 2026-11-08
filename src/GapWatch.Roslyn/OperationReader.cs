using GapWatch.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static GapWatch.Roslyn.SyntaxHelpers;

namespace GapWatch.Roslyn;

/// <summary>Describes one endpoint found in code as an <see cref="OperationContract"/>.</summary>
/// <remarks>
/// Binding follows ASP.NET Core: an explicit <c>[From*]</c> attribute wins; otherwise a name in the route template is a
/// path parameter, a simple type is a query parameter, and a type declared in the project is the body (for an
/// <c>[ApiController]</c> action, or a minimal API endpoint whose method allows a body). Anything else, such as
/// <c>CancellationToken</c> or a service interface, is not part of the contract.
/// </remarks>
internal sealed partial class OperationReader(Compilation compilation, SchemaMapper schemas)
{
    private const string Json = "application/json";
    private const string ProblemJson = "application/problem+json";
    private const string ProblemName = "ProblemDetails";
    private const string ValidationProblemName = "HttpValidationProblemDetails";

    private static readonly HashSet<string> MethodsWithoutInferredBody = new(StringComparer.Ordinal) { "GET", "HEAD", "DELETE", "OPTIONS" };

    /// <summary>
    /// ASP.NET Core's typed results (<c>Results&lt;Ok&lt;Pet&gt;, NotFound&gt;</c>, <c>TypedResults.Ok(pet)</c>) that
    /// describe their own response: the status code, and which argument of the <c>TypedResults</c> method is the value.
    /// </summary>
    private static readonly Dictionary<string, (int Status, int ValueArgument)> TypedResults = new(StringComparer.Ordinal)
    {
        ["Ok"] = (200, 0),
        ["Created"] = (201, 1),
        ["CreatedAtRoute"] = (201, 0),
        ["Accepted"] = (202, 1),
        ["AcceptedAtRoute"] = (202, 0),
        ["NoContent"] = (204, -1),
        ["BadRequest"] = (400, 0),
        ["NotFound"] = (404, 0),
        ["Conflict"] = (409, 0),
        ["UnprocessableEntity"] = (422, 0),
        ["InternalServerError"] = (500, 0),
        ["ValidationProblem"] = (400, -1),
    };

    /// <summary>
    /// The operations of one endpoint: one, or one per API version it maps to (Asp.Versioning). A route with a
    /// <c>{version:apiVersion}</c> segment gets the version in its path (<c>/api/v1/pets</c>), as the API explorer
    /// substitutes it; otherwise the operation carries the version (<c>GET /pets [1.0]</c>), as it does in one document
    /// per version. Every operation of a deprecated version is deprecated, so GW061 and GW063 apply.
    /// </summary>
    public IEnumerable<OperationContract> ReadAll(EndpointDeclaration endpoint)
    {
        var versions = ApiVersions(endpoint);
        if (versions.Count == 0)
        {
            yield return Read(endpoint);
            yield break;
        }

        foreach (var (version, deprecated) in versions)
        {
            var route = ApiVersionSegment().Replace(endpoint.Route, UrlSegment(version));
            var operation = Read(endpoint with { Route = route });
            yield return operation with
            {
                Deprecated = operation.Deprecated || deprecated,
                ApiVersion = route == endpoint.Route ? version : null,
            };
        }
    }

    public OperationContract Read(EndpointDeclaration endpoint)
    {
        var model = compilation.GetSemanticModel(endpoint.Location.SourceTree!);
        var attributes = endpoint.Attributes;
        var all = attributes.Concat(endpoint.TypeAttributes).ToList();

        var (parameters, body) = ReadParameters(endpoint);
        return new OperationContract(
            endpoint.Method,
            RouteTemplate.ToOpenApiPath(endpoint.Route),
            OperationId(endpoint, model),
            all.Has("Obsolete"),
            parameters,
            body,
            ReadResponses(endpoint, model),
            Security(endpoint))
        {
            Position = Position(endpoint.Location),
        };
    }

    // --- Parameters and body -------------------------------------------------------------------------------------

    private enum Source
    {
        None,
        Path,
        Query,
        Header,
        Body,
        Form,
    }

    private (List<ParameterContract> Parameters, RequestBodyContract? Body) ReadParameters(EndpointDeclaration endpoint)
    {
        var route = RouteTemplate.Parameters(endpoint.Route);
        var parameters = new List<ParameterContract>();
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RequestBodyContract? body = null;
        var form = new List<(string Name, SchemaContract Schema, bool Required, SourcePosition Position)>();

        foreach (var candidate in endpoint.Parameters.SelectMany(Candidates))
        {
            var (symbolName, type, attributes, model, location, hasDefault, isRequired) = candidate;
            var source = SourceOf(symbolName, type, attributes, endpoint, route);
            var explicitName = attributes
                .Where(a => Name(a) is "FromQuery" or "FromRoute" or "FromHeader" or "FromForm")
                .Select(a => StringProperty(a, "Name", model))
                .FirstOrDefault(n => n is not null);
            var name = explicitName ?? symbolName;
            var position = Position(location);
            var required = isRequired || IsRequired(type, attributes, hasDefault);

            switch (source)
            {
                case Source.Path:
                    var declared = route.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).Name ?? name;
                    bound.Add(declared);
                    parameters.Add(new ParameterContract(declared, "path", true, schemas.Map(type, attributes)) { Position = position });
                    break;
                case Source.Query:
                    parameters.Add(new ParameterContract(name, "query", required, schemas.Map(type, attributes)) { Position = position });
                    break;
                case Source.Header:
                    parameters.Add(new ParameterContract(name, "header", required, schemas.Map(type, attributes)) { Position = position });
                    break;
                case Source.Form:
                    form.Add((name, FormSchema(type, attributes), required, position));
                    break;
                case Source.Body:
                    var schema = schemas.Map(type, attributes);
                    body = new RequestBodyContract(required, ContentTypes(Consumes(endpoint, model), [Json]).ToDictionary(t => t, _ => schema))
                    {
                        Position = position,
                    };
                    break;
            }
        }

        // Route parameters no handler parameter binds still exist in the URL, typed by their constraint.
        foreach (var (name, constraint) in route)
        {
            if (bound.Add(name))
            {
                parameters.Add(new ParameterContract(name, "path", true, ConstraintSchema(constraint)) { Position = Position(endpoint.Location) });
            }
        }

        if (form.Count > 0)
        {
            var schema = form is [{ Schema.ReferenceName: not null } single] ? single.Schema : new SchemaContract
            {
                Types = ["object"],
                Properties = form.ToDictionary(
                    f => f.Name,
                    f => new PropertyContract(f.Schema, f.Required, ReadOnly: false, WriteOnly: false) { Position = f.Position },
                    StringComparer.Ordinal),
            };
            body = new RequestBodyContract(form.Any(f => f.Required), new Dictionary<string, SchemaContract> { ["multipart/form-data"] = schema })
            {
                Position = form[0].Position,
            };
        }

        if (body is null && Accepts(endpoint) is { } accepts)
        {
            var schema = accepts.Type is null ? SchemaContract.Unspecified : schemas.Map(accepts.Type);
            body = new RequestBodyContract(true, ContentTypes(accepts.ContentTypes, [Json]).ToDictionary(t => t, _ => schema))
            {
                Position = Position(accepts.Call.GetLocation()),
            };
        }

        return (parameters, body);
    }

    /// <summary>One value the handler binds: a parameter, or a property of an <c>[AsParameters]</c> type.</summary>
    private sealed record Candidate(
        string Name,
        ITypeSymbol Type,
        IReadOnlyList<AttributeSyntax> Attributes,
        SemanticModel Model,
        Location Location,
        bool HasDefault,
        bool IsRequired = false);

    /// <summary>
    /// The parameter itself, or for <c>[AsParameters]</c> the public settable properties of its type (and the
    /// parameters of a positional record), each bound as if it were a parameter.
    /// </summary>
    private IEnumerable<Candidate> Candidates(ParameterSyntax syntax)
    {
        var model = compilation.GetSemanticModel(syntax.SyntaxTree);
        if (model.GetDeclaredSymbol(syntax) is not { } symbol)
        {
            yield break;
        }

        var attributes = Attributes(syntax.AttributeLists).ToList();
        if (!attributes.Has("AsParameters"))
        {
            yield return new Candidate(symbol.Name, symbol.Type, attributes, model, syntax.Identifier.GetLocation(), symbol.HasExplicitDefaultValue);
            yield break;
        }

        if (symbol.Type is not INamedTypeSymbol type || !SchemaMapper.IsDeclaredInSource(type))
        {
            yield break;
        }

        var constructor = type.InstanceConstructors.FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0);
        if (constructor is not null)
        {
            foreach (var parameter in constructor.Parameters)
            {
                if (parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ParameterSyntax declaration)
                {
                    yield return new Candidate(
                        parameter.Name,
                        parameter.Type,
                        Attributes(declaration.AttributeLists).ToList(),
                        compilation.GetSemanticModel(declaration.SyntaxTree),
                        declaration.Identifier.GetLocation(),
                        parameter.HasExplicitDefaultValue);
                }
            }

            yield break;
        }

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.DeclaredAccessibility != Accessibility.Public || property.SetMethod is null
                || property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not PropertyDeclarationSyntax declaration)
            {
                continue;
            }

            yield return new Candidate(
                property.Name,
                property.Type,
                Attributes(declaration.AttributeLists).ToList(),
                compilation.GetSemanticModel(declaration.SyntaxTree),
                declaration.Identifier.GetLocation(),
                HasDefault: declaration.Initializer is not null,
                IsRequired: property.IsRequired);
        }
    }

    private static Source SourceOf(
        string name,
        ITypeSymbol type,
        IReadOnlyList<AttributeSyntax> attributes,
        EndpointDeclaration endpoint,
        IReadOnlyList<(string Name, string? Constraint)> route)
    {
        foreach (var attribute in attributes)
        {
            switch (Name(attribute))
            {
                case "FromRoute": return Source.Path;
                case "FromQuery": return Source.Query;
                case "FromHeader": return Source.Header;
                case "FromBody": return Source.Body;
                case "FromForm": return Source.Form;
                case "FromServices" or "FromKeyedServices" or "AsParameters": return Source.None;
            }
        }

        if (type.TypeKind == TypeKind.Error)
        {
            return type.Name is "IFormFile" or "IFormFileCollection" ? Source.Form : Source.None;
        }

        if (route.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Source.Path;
        }

        if (SchemaMapper.IsSimple(type) || SchemaMapper.SimpleElementType(type) is not null)
        {
            return Source.Query;
        }

        var declaredInProject = SchemaMapper.IsDeclaredInSource(type)
                                || (type is INamedTypeSymbol named && SchemaMapper.ElementType(named) is { } element && SchemaMapper.IsDeclaredInSource(element))
                                || (type is IArrayTypeSymbol array && SchemaMapper.IsDeclaredInSource(array.ElementType));
        if (!declaredInProject || type.TypeKind == TypeKind.Interface || type.IsAbstract && type.TypeKind == TypeKind.Class)
        {
            return Source.None;
        }

        return endpoint.IsController
            ? endpoint.IsApiController ? Source.Body : Source.None
            : MethodsWithoutInferredBody.Contains(endpoint.Method) ? Source.None : Source.Body;
    }

    /// <summary>
    /// <c>[Required]</c> or <c>[BindRequired]</c> makes a value required; otherwise it is required unless it is nullable
    /// (<c>int?</c>, <c>string?</c>) or has a default value, as ASP.NET Core binding treats it.
    /// </summary>
    private static bool IsRequired(ITypeSymbol type, IReadOnlyList<AttributeSyntax> attributes, bool hasDefault) =>
        attributes.Has("Required") || attributes.Has("BindRequired")
        || !(hasDefault || IsNullable(type));

    private static bool IsNullable(ITypeSymbol type) =>
        type.NullableAnnotation == NullableAnnotation.Annotated
        || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private SchemaContract FormSchema(ITypeSymbol type, IReadOnlyList<AttributeSyntax> attributes) => type.Name switch
    {
        "IFormFile" when type.TypeKind == TypeKind.Error => new SchemaContract { Types = ["string"], Format = "binary" },
        "IFormFileCollection" when type.TypeKind == TypeKind.Error => new SchemaContract
        {
            Types = ["array"],
            Items = new SchemaContract { Types = ["string"], Format = "binary" },
        },
        _ => schemas.Map(type, attributes),
    };

    /// <summary>Route constraints that say what type a parameter is; everything else is a string.</summary>
    private static SchemaContract ConstraintSchema(string? constraint) => constraint?.ToLowerInvariant() switch
    {
        "int" => new SchemaContract { Types = ["integer"], Format = "int32" },
        "long" => new SchemaContract { Types = ["integer"], Format = "int64" },
        "bool" => new SchemaContract { Types = ["boolean"] },
        "guid" => new SchemaContract { Types = ["string"], Format = "uuid" },
        "datetime" => new SchemaContract { Types = ["string"], Format = "date-time" },
        "decimal" or "double" => new SchemaContract { Types = ["number"], Format = "double" },
        "float" => new SchemaContract { Types = ["number"], Format = "float" },
        _ => new SchemaContract { Types = ["string"] },
    };

    /// <summary><c>[Consumes("...")]</c> on the action, or else on the controller.</summary>
    private static IReadOnlyList<string>? Consumes(EndpointDeclaration endpoint, SemanticModel model) =>
        ContentTypeAttribute(endpoint.Attributes, "Consumes", model) ?? ContentTypeAttribute(endpoint.TypeAttributes, "Consumes", model);

    /// <summary>A minimal API <c>.Accepts&lt;T&gt;("content/type")</c> or <c>.Accepts(typeof(T), "content/type")</c>.</summary>
    private (ITypeSymbol? Type, IReadOnlyList<string> ContentTypes, InvocationExpressionSyntax Call)? Accepts(EndpointDeclaration endpoint)
    {
        var call = endpoint.Conventions.LastOrDefault(c => MethodName(c) == "Accepts");
        if (call is null)
        {
            return null;
        }

        var model = compilation.GetSemanticModel(call.SyntaxTree);
        var arguments = call.ArgumentList.Arguments.Select(a => a.Expression).ToList();
        var type = TypeArguments(call) is [var generic] ? model.GetTypeInfo(generic).Type : arguments.Count > 0 ? TypeOf(arguments[0], model) : null;
        var contentTypes = arguments.Select(a => ConstantString(a, model)).OfType<string>().ToList();
        return (type, contentTypes, call);
    }

    // --- Responses -----------------------------------------------------------------------------------------------

    /// <summary>One response the code declares or implies.</summary>
    /// <param name="Status">Status code, or <c>DEFAULT</c>.</param>
    /// <param name="Type">The value's type, when known.</param>
    /// <param name="ContentTypes">Declared content types, or null for the default.</param>
    /// <param name="Problem">For a problem response, the schema name: <c>ProblemDetails</c> or <c>HttpValidationProblemDetails</c>.</param>
    /// <param name="Location">Where it is declared: the attribute, the call, or the endpoint.</param>
    /// <param name="HasType">Whether the response has a body.</param>
    private sealed record Declared(string Status, ITypeSymbol? Type, IReadOnlyList<string>? ContentTypes, string? Problem, Location Location, bool HasType);

    private List<ResponseContract> ReadResponses(EndpointDeclaration endpoint, SemanticModel model)
    {
        var inferred = Inferred(endpoint, model).ToList();
        var explicitResponses = endpoint.IsController ? AttributeResponses(endpoint, model).ToList() : ConventionResponses(endpoint).ToList();
        var produces = endpoint.IsController
            ? ContentTypeAttribute(endpoint.Attributes, "Produces", model) ?? ContentTypeAttribute(endpoint.TypeAttributes, "Produces", model)
            : null;

        // A declared success status without a type takes the type the action returns, as ActionResult<T> does.
        var inferredType = inferred.FirstOrDefault(r => r.Status == "200" && r.HasType);
        var declared = explicitResponses
            .Select(r => !r.HasType && IsSuccess(r.Status) && inferredType is not null && r.Problem is null ? r with { Type = inferredType.Type, HasType = true } : r)
            .ToList();

        // Controllers: explicit attributes replace what the return type implies, unless none of them is a success.
        // Minimal APIs: typed results and conventions add up, a convention winning for the same status.
        var useInferred = endpoint.IsController ? !declared.Any(r => IsSuccess(r.Status)) : true;
        var all = new List<Declared>(declared);
        if (useInferred)
        {
            all.AddRange(inferred.Where(i => !declared.Any(d => d.Status == i.Status)));
        }

        if (all.Count == 0)
        {
            all.Add(new Declared("200", null, null, null, endpoint.Location, false));
        }

        return all
            .GroupBy(r => r.Status, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.Status, StringComparer.Ordinal)
            .Select(r => new ResponseContract(r.Status, Content(r, produces)) { Position = Position(r.Location) })
            .ToList();
    }

    private Dictionary<string, SchemaContract> Content(Declared response, IReadOnlyList<string>? produces)
    {
        if (response.Problem is { } problemName)
        {
            var problem = ProblemDetails(validation: problemName == ValidationProblemName);
            return ContentTypes(response.ContentTypes, [ProblemJson]).ToDictionary(t => t, _ => problem, StringComparer.Ordinal);
        }

        if (!response.HasType || response.Type is null || response.Type.SpecialType == SpecialType.System_Void)
        {
            return new Dictionary<string, SchemaContract>(StringComparer.Ordinal);
        }

        var schema = response.Type.Name is "ProblemDetails" or "ValidationProblemDetails" or "HttpValidationProblemDetails"
                     && response.Type.TypeKind == TypeKind.Error
            ? ProblemDetails(validation: response.Type.Name != ProblemName)
            : schemas.Map(response.Type);
        var fallback = response.Type.SpecialType == SpecialType.System_String ? "text/plain" : Json;
        return ContentTypes(response.ContentTypes ?? produces, [fallback]).ToDictionary(t => t, _ => schema, StringComparer.Ordinal);
    }

    /// <summary><c>[ProducesResponseType]</c>, <c>[ProducesDefaultResponseType]</c> and <c>[Produces(typeof(T))]</c> on the action and the controller.</summary>
    private IEnumerable<Declared> AttributeResponses(EndpointDeclaration endpoint, SemanticModel actionModel)
    {
        foreach (var attribute in endpoint.Attributes.Concat(endpoint.TypeAttributes))
        {
            var model = compilation.GetSemanticModel(attribute.SyntaxTree);
            var name = Name(attribute);
            var positional = Positional(attribute).Select(a => a.Expression).ToList();
            var generic = TypeArguments(attribute) is [var argument] ? model.GetTypeInfo(argument).Type : null;

            switch (name)
            {
                case "ProducesResponseType":
                    var typed = generic ?? (positional.Count > 0 ? TypeOf(positional[0], model) : null) ?? (Property(attribute, "Type") is { } t ? TypeOf(t, model) : null);
                    var rest = positional.Skip(generic is null && positional.Count > 0 && positional[0] is TypeOfExpressionSyntax ? 1 : 0).ToList();
                    var status = (rest.Count > 0 ? StatusCode(rest[0], model) : null) ?? (Property(attribute, "StatusCode") is { } s ? StatusCode(s, model) : null);
                    if (status is null)
                    {
                        continue;
                    }

                    var contentTypes = rest.Skip(1).Select(e => ConstantString(e, model)).OfType<string>().ToList();
                    yield return new Declared(
                        status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        typed,
                        contentTypes.Count > 0 ? contentTypes : null,
                        Problem: null,
                        attribute.GetLocation(),
                        HasType: typed is not null);
                    break;

                case "ProducesDefaultResponseType":
                    var defaultType = generic ?? (positional.Count > 0 ? TypeOf(positional[0], model) : null);
                    yield return new Declared("DEFAULT", defaultType, null, defaultType is null ? ProblemName : null, attribute.GetLocation(), HasType: defaultType is not null);
                    break;

                case "Produces":
                    var producedType = generic ?? (positional.Count > 0 ? TypeOf(positional[0], model) : null) ?? (Property(attribute, "Type") is { } p ? TypeOf(p, model) : null);
                    if (producedType is not null)
                    {
                        yield return new Declared("200", producedType, null, null, attribute.GetLocation(), HasType: true);
                    }

                    break;
            }
        }
    }

    /// <summary><c>.Produces&lt;T&gt;(status)</c>, <c>.Produces(status, typeof(T))</c>, <c>.ProducesProblem(status)</c> and <c>.ProducesValidationProblem()</c>.</summary>
    private IEnumerable<Declared> ConventionResponses(EndpointDeclaration endpoint)
    {
        foreach (var call in endpoint.Conventions)
        {
            var model = compilation.GetSemanticModel(call.SyntaxTree);
            var arguments = call.ArgumentList.Arguments;
            ExpressionSyntax? Argument(string name, int index) =>
                arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name)?.Expression
                ?? (index < arguments.Count && arguments[index].NameColon is null ? arguments[index].Expression : null);
            string StatusOr(int fallback, int index) =>
                ((Argument("statusCode", index) is { } e ? StatusCode(e, model) : null) ?? fallback).ToString(System.Globalization.CultureInfo.InvariantCulture);
            IReadOnlyList<string>? Types(int from) =>
                arguments.Skip(from).Select(a => ConstantString(a.Expression, model)).OfType<string>().ToList() is { Count: > 0 } list ? list : null;

            switch (MethodName(call))
            {
                case "Produces":
                    if (TypeArguments(call) is [var generic])
                    {
                        var type = model.GetTypeInfo(generic).Type;
                        yield return new Declared(StatusOr(200, 0), type, Types(1), null, call.GetLocation(), HasType: type is not null);
                    }
                    else
                    {
                        var type = Argument("responseType", 1) is { } t ? TypeOf(t, model) : null;
                        yield return new Declared(StatusOr(200, 0), type, Types(2), null, call.GetLocation(), HasType: type is not null);
                    }

                    break;
                case "ProducesProblem":
                    yield return new Declared(StatusOr(500, 0), null, Types(1), ProblemName, call.GetLocation(), HasType: false);
                    break;
                case "ProducesValidationProblem":
                    yield return new Declared(StatusOr(400, 0), null, Types(1), ValidationProblemName, call.GetLocation(), HasType: false);
                    break;
            }
        }
    }

    /// <summary>The responses the handler's return type implies: <c>T</c>, <c>Task&lt;T&gt;</c>, <c>ActionResult&lt;T&gt;</c> or typed results.</summary>
    private IEnumerable<Declared> Inferred(EndpointDeclaration endpoint, SemanticModel model)
    {
        if (endpoint.Lambda is { } lambda && lambda is not ParenthesizedLambdaExpressionSyntax { ReturnType: not null })
        {
            var expressions = ReturnedExpressions(lambda).ToList();
            if (expressions.Count == 0)
            {
                return [Empty(endpoint.Location)];
            }

            var lambdaModel = compilation.GetSemanticModel(lambda.SyntaxTree);
            return expressions.SelectMany(e => FromExpression(e, lambdaModel, endpoint.Location));
        }

        return endpoint.Handler is { } handler ? FromType(handler.ReturnType, endpoint.Location) : [];
    }

    /// <summary>Return values of a lambda: its expression body, or every <c>return</c> outside nested functions.</summary>
    private static IEnumerable<ExpressionSyntax> ReturnedExpressions(AnonymousFunctionExpressionSyntax lambda)
    {
        if (lambda.ExpressionBody is { } expression)
        {
            return [expression];
        }

        return lambda.Block is null
            ? []
            : lambda.Block.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .OfType<ReturnStatementSyntax>()
                .Select(r => r.Expression)
                .OfType<ExpressionSyntax>();
    }

    private IEnumerable<Declared> FromExpression(ExpressionSyntax expression, SemanticModel model, Location location)
    {
        while (expression is AwaitExpressionSyntax or ParenthesizedExpressionSyntax)
        {
            expression = expression is AwaitExpressionSyntax a ? a.Expression : ((ParenthesizedExpressionSyntax)expression).Expression;
        }

        // TypedResults.Ok(pet): the lambda returns Ok<Pet>, which describes itself. Results.Ok(pet) returns IResult.
        if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: var factory } } member } call
            && factory is "TypedResults" or "Results")
        {
            if (factory == "TypedResults" && TypedResults.TryGetValue(member.Name.Identifier.ValueText, out var result))
            {
                var arguments = call.ArgumentList.Arguments;
                var value = result.ValueArgument >= 0 && result.ValueArgument < arguments.Count
                    ? model.GetTypeInfo(arguments[result.ValueArgument].Expression).Type
                    : null;
                return [Typed(member.Name.Identifier.ValueText, result.Status, value, location)];
            }

            return [Empty(location)];
        }

        var type = model.GetTypeInfo(expression).Type;
        return type is null || type.TypeKind == TypeKind.Error
            ? [Empty(location)]
            : FromType(type, location);
    }

    private IEnumerable<Declared> FromType(ITypeSymbol type, Location location)
    {
        if (type.SpecialType == SpecialType.System_Void)
        {
            return [Empty(location)];
        }

        if (type is INamedTypeSymbol { Name: "Task" or "ValueTask" } task && task.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
        {
            return task.Arity == 1 ? FromType(task.TypeArguments[0], location) : [Empty(location)];
        }

        if (type.TypeKind != TypeKind.Error)
        {
            return [new Declared("200", type, null, null, location, true)];
        }

        var named = type as INamedTypeSymbol;
        if (type.Name == "ActionResult" && named is { Arity: 1 })
        {
            return [new Declared("200", named.TypeArguments[0], null, null, location, true)];
        }

        if (type.Name == "Results" && named is { Arity: > 0 })
        {
            return named.TypeArguments.SelectMany(t => FromTypedResult(t, location));
        }

        return TypedResults.ContainsKey(type.Name)
            ? FromTypedResult(type, location)
            : [Empty(location)];
    }

    /// <summary>A 200 response without content: <c>void</c>, <c>Task</c>, <c>IActionResult</c>, <c>IResult</c>.</summary>
    private static Declared Empty(Location location) => new("200", null, null, null, location, false);

    private static IEnumerable<Declared> FromTypedResult(ITypeSymbol type, Location location)
    {
        if (!TypedResults.TryGetValue(type.Name, out var result))
        {
            return [];
        }

        var value = type is INamedTypeSymbol { Arity: 1 } generic ? generic.TypeArguments[0] : null;
        return [Typed(type.Name, result.Status, value, location)];
    }

    private static Declared Typed(string name, int status, ITypeSymbol? value, Location location) =>
        name == "ValidationProblem"
            ? new Declared("400", null, null, ValidationProblemName, location, HasType: false)
            : new Declared(status.ToString(System.Globalization.CultureInfo.InvariantCulture), value, null, null, location, HasType: value is not null);

    /// <summary>ASP.NET Core's <c>ProblemDetails</c> (RFC 9457), or <c>HttpValidationProblemDetails</c> with its <c>errors</c>.</summary>
    private static SchemaContract ProblemDetails(bool validation)
    {
        static PropertyContract Optional(string type, string? format = null) =>
            new(new SchemaContract { Types = [type], Format = format, Nullable = true }, false, false, false);

        var properties = new Dictionary<string, PropertyContract>(StringComparer.Ordinal)
        {
            ["type"] = Optional("string"),
            ["title"] = Optional("string"),
            ["status"] = Optional("integer", "int32"),
            ["detail"] = Optional("string"),
            ["instance"] = Optional("string"),
        };
        if (validation)
        {
            properties["errors"] = new PropertyContract(new SchemaContract { Types = ["object"] }, false, false, false);
        }

        return new SchemaContract
        {
            Types = ["object"],
            ReferenceName = validation ? ValidationProblemName : ProblemName,
            Properties = properties,
        };
    }

    // --- Operation id and security -------------------------------------------------------------------------------

    /// <summary>
    /// The endpoint name, which <c>Microsoft.AspNetCore.OpenApi</c> uses as the operationId: <c>WithName("...")</c>,
    /// <c>[EndpointName("...")]</c>, or the route name in <c>[HttpGet(Name = "...")]</c> and <c>[Route(Name = "...")]</c>.
    /// </summary>
    private string? OperationId(EndpointDeclaration endpoint, SemanticModel model)
    {
        foreach (var call in endpoint.Conventions.Reverse())
        {
            if (MethodName(call) == "WithName" && call.ArgumentList.Arguments.Count > 0
                && ConstantString(call.ArgumentList.Arguments[0].Expression, compilation.GetSemanticModel(call.SyntaxTree)) is { } name)
            {
                return name;
            }
        }

        foreach (var attribute in endpoint.Attributes)
        {
            var attributeName = Name(attribute);
            if (attributeName == "EndpointName" && FirstStringArgument(attribute, model) is { } endpointName)
            {
                return endpointName;
            }

            if ((attributeName.StartsWith("Http", StringComparison.Ordinal) || attributeName == "Route")
                && StringProperty(attribute, "Name", model) is { } routeName)
            {
                return routeName;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>[AllowAnonymous]</c> or <c>AllowAnonymous()</c> anywhere allows anonymous access. Otherwise every
    /// <c>[Authorize]</c> and <c>RequireAuthorization</c> applies at once, so together they are one alternative:
    /// <c>authenticated[]</c> for a bare one, <c>policy[Admin]</c>, <c>roles[Admin,Ops]</c>, <c>schemes[Bearer]</c>.
    /// </summary>
    private IReadOnlyList<string> Security(EndpointDeclaration endpoint)
    {
        var attributes = endpoint.Attributes.Concat(endpoint.TypeAttributes).ToList();
        if (attributes.Has("AllowAnonymous") || endpoint.Conventions.Any(c => MethodName(c) == "AllowAnonymous"))
        {
            return [];
        }

        var parts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes.Named("Authorize"))
        {
            var attributeModel = compilation.GetSemanticModel(attribute.SyntaxTree);
            var policy = FirstStringArgument(attribute, attributeModel) ?? StringProperty(attribute, "Policy", attributeModel);
            var roles = StringProperty(attribute, "Roles", attributeModel);
            var authenticationSchemes = StringProperty(attribute, "AuthenticationSchemes", attributeModel);
            AddParts(parts, policy, roles, authenticationSchemes);
        }

        foreach (var call in endpoint.Conventions.Where(c => MethodName(c) == "RequireAuthorization"))
        {
            var callModel = compilation.GetSemanticModel(call.SyntaxTree);
            var policies = call.ArgumentList.Arguments.Select(a => ConstantString(a.Expression, callModel)).OfType<string>().ToList();
            if (policies.Count == 0)
            {
                parts.Add("authenticated[]");
            }

            foreach (var policy in policies)
            {
                parts.Add($"policy[{policy}]");
            }
        }

        return parts.Count == 0 ? [] : [string.Join("+", parts)];
    }

    private static void AddParts(SortedSet<string> parts, string? policy, string? roles, string? schemes)
    {
        if (policy is null && roles is null && schemes is null)
        {
            parts.Add("authenticated[]");
            return;
        }

        if (policy is not null)
        {
            parts.Add($"policy[{policy}]");
        }

        if (roles is not null)
        {
            parts.Add($"roles[{List(roles)}]");
        }

        if (schemes is not null)
        {
            parts.Add($"schemes[{List(schemes)}]");
        }

        static string List(string value) => string.Join(",", value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal));
    }

    // --- API versions --------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>[ApiVersion]</c> on the controller or action (with <c>Deprecated = true</c>), narrowed by
    /// <c>[MapToApiVersion]</c>; for minimal APIs, <c>HasApiVersion</c>, <c>HasDeprecatedApiVersion</c> and
    /// <c>MapToApiVersion</c> calls. <c>[ApiVersionNeutral]</c> means none. Version sets built with
    /// <c>NewApiVersionSet()</c> are not followed.
    /// </summary>
    private List<(string Version, bool Deprecated)> ApiVersions(EndpointDeclaration endpoint)
    {
        var attributes = endpoint.Attributes.Concat(endpoint.TypeAttributes).ToList();
        if (attributes.Has("ApiVersionNeutral"))
        {
            return [];
        }

        var declared = new List<(string Version, bool Deprecated)>();
        var mapped = new List<string>();
        foreach (var attribute in attributes)
        {
            var model = compilation.GetSemanticModel(attribute.SyntaxTree);
            var value = Positional(attribute).FirstOrDefault()?.Expression is { } expression ? VersionText(expression, model) : null;
            switch (Name(attribute))
            {
                case "ApiVersion" when value is not null:
                    declared.Add((value, Property(attribute, "Deprecated") is { } d && model.GetConstantValue(d) is { HasValue: true, Value: true }));
                    break;
                case "MapToApiVersion" when value is not null:
                    mapped.Add(value);
                    break;
            }
        }

        foreach (var call in endpoint.Conventions)
        {
            var model = compilation.GetSemanticModel(call.SyntaxTree);
            var value = call.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } expression ? VersionText(expression, model) : null;
            switch (MethodName(call))
            {
                case "HasApiVersion" when value is not null:
                    declared.Add((value, false));
                    break;
                case "HasDeprecatedApiVersion" when value is not null:
                    declared.Add((value, true));
                    break;
                case "MapToApiVersion" when value is not null:
                    mapped.Add(value);
                    break;
            }
        }

        List<(string Version, bool Deprecated)> versions = mapped.Count == 0
            ? declared
            : mapped.Select(m => declared.Where(d => SameVersion(d.Version, m)).Select(d => d with { Version = m }).FirstOrDefault((m, false))).ToList();
        return versions
            .GroupBy(v => UrlSegment(v.Version), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Version, g.Any(v => v.Deprecated)))
            .ToList();
    }

    /// <summary><c>"1.0"</c>, <c>1.0</c>, <c>new ApiVersion(1, 0)</c> or <c>new ApiVersion(2)</c> as text.</summary>
    private static string? VersionText(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: { Count: > 0 } arguments })
        {
            var parts = arguments.Select(a => model.GetConstantValue(a.Expression)).ToList();
            if (parts.All(p => p is { HasValue: true, Value: int or double }))
            {
                return parts.Count == 1 && parts[0].Value is double number
                    ? Number(number)
                    : string.Join(".", parts.Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)));
            }

            return parts[0] is { HasValue: true, Value: string text } ? text : null;
        }

        return model.GetConstantValue(expression) switch
        {
            { HasValue: true, Value: string text } => text,
            { HasValue: true, Value: double number } => Number(number),
            { HasValue: true, Value: int number } => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };

        static string Number(double value) => value.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>VVV</c> format the API explorer substitutes in URLs: <c>1.0</c> is <c>1</c>, <c>1.1</c> stays.</summary>
    private static string UrlSegment(string version) =>
        version.EndsWith(".0", StringComparison.Ordinal) ? version[..^2] : version;

    private static bool SameVersion(string a, string b) => string.Equals(UrlSegment(a), UrlSegment(b), StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(@"\{[^{}:]+:apiVersion\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ApiVersionSegment();

    // --- Shared helpers ------------------------------------------------------------------------------------------

    private static bool IsSuccess(string status) => status.StartsWith('2');

    /// <summary>The content types of an attribute such as <c>[Produces("application/json", "text/xml")]</c>.</summary>
    private static IReadOnlyList<string>? ContentTypeAttribute(IEnumerable<AttributeSyntax> attributes, string name, SemanticModel model)
    {
        foreach (var attribute in attributes.Named(name))
        {
            var types = Positional(attribute)
                .Select(a => ConstantString(a.Expression, model.Compilation.GetSemanticModel(attribute.SyntaxTree)))
                .OfType<string>()
                .ToList();
            if (types.Count > 0)
            {
                return types;
            }
        }

        return null;
    }

    private static IEnumerable<string> ContentTypes(IReadOnlyList<string>? declared, IReadOnlyList<string> fallback) =>
        (declared is { Count: > 0 } ? declared : fallback).Select(t => t.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal);
}
