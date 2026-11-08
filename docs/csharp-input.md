# Reading the contract from C#

With `--input csharp`, GapWatch builds the contract straight from ASP.NET Core source, so a team that does not commit an OpenAPI document can still compare two versions of its API:

```
gapwatch compare src/Petstore.Api src/Petstore.Api --input csharp --base-ref origin/main
```

`<base>` and `<head>` are folders of C# source. With `--base-ref`, the base folder is read from that git revision: every `.cs` file under it, in one `git cat-file --batch`, without a checkout. Without it, pass two folders.

All rules apply unchanged. Operation and location strings have the same form as for an OpenAPI document (`GET /api/Pets/{id}`, `parameter 'limit' (query)`, `response 200 application/json: $.name`). Suppressions, baselines and SARIF fingerprints written for a document therefore keep matching after a switch to C# input, provided the code describes the same API. Findings point at the C# file and line directly, so `--source` is not needed.

## Why consistency matters more than matching a generator

Both sides are read by the same reader. If the reader misses a detail, it misses it in both versions, so the result is a missed finding, rarely a false one. The reader follows what `Microsoft.AspNetCore.OpenApi` documents, but it does not try to reproduce Swashbuckle or NSwag byte for byte. Comparing a document from one tool with code read by GapWatch is not supported; use one input kind for both sides.

## What is read

| C# | Contract |
|---|---|
| Controllers: `[Http*]`, `[Route]` (class, base class and action), `[controller]`/`[action]`/`[area]` tokens, `~/` and `/` overrides | Operations. The route becomes an OpenAPI path, and constraints, defaults and optional markers are removed. |
| Minimal APIs: `MapGet`/`MapPost`/`MapPut`/`MapDelete`/`MapPatch`, `MapGroup` prefixes (chained, or held in a local or field) | Operations. Calls chained after `Map*`, and statements on a group variable (`pets.RequireAuthorization()`), apply to the endpoint. |
| `[FromRoute]`, `[FromQuery]`, `[FromHeader]`, `[FromBody]`, `[FromForm]`, with `Name = "..."` | Parameters and body, from the given source. |
| A parameter without an attribute | A name in the route is a path parameter. A simple type (primitives, enums, `Guid`, dates, and collections of them) is a query parameter. A type declared in the project is the body on an `[ApiController]` action, or on a minimal API whose method allows a body (not GET, HEAD, DELETE or OPTIONS). Anything else, such as `CancellationToken`, services and unresolved types, is not part of the contract. |
| `[AsParameters]` | Each property, or positional record parameter, bound as its own parameter. |
| `IFormFile`, `IFormFileCollection`, `[FromForm]` | A `multipart/form-data` body. |
| Required | A path parameter is always required. Otherwise a value is required unless it is nullable (`int?`, `string?`) or has a default value. `[Required]` and `[BindRequired]` force it. |
| Return type: `T`, `Task<T>`, `ValueTask<T>`, `ActionResult<T>`, `Results<Ok<T>, NotFound>`, `TypedResults.Ok(x)` returned by a lambda | Responses. `void`, `Task`, `IActionResult` and `IResult` give `200` without content, as ASP.NET Core returns. A `string` is `text/plain`, and everything else is `application/json`. |
| `[ProducesResponseType]` (`typeof`, generic, `StatusCodes.Status404NotFound`), `[Produces]`, `[ProducesDefaultResponseType]`, `[Consumes]` | Declared responses and media types. On a controller, declared success responses replace the one the return type implies. A declared success response without a type takes the return type, as `ActionResult<T>` does. |
| `.Produces<T>(status)`, `.Produces(status)`, `.ProducesProblem(status)`, `.ProducesValidationProblem()`, `.Accepts<T>(...)` | Minimal API responses and body. Problem responses are `application/problem+json` with the `ProblemDetails` schema. |
| `[Authorize]`, `[Authorize(Policy = ...)]`, `Roles`, `AuthenticationSchemes`, `RequireAuthorization(...)`; `[AllowAnonymous]`, `AllowAnonymous()` | Security. Every requirement applies at once, so together they form one alternative, for example `authenticated[]+policy[Admin]+roles[Ops,Owner]`. `[AllowAnonymous]` anywhere means open. |
| `[Obsolete]` on the action, controller or handler | Deprecated (GW002). |
| `WithName("...")`, `[EndpointName]`, `[HttpGet(Name = "...")]`, `[Route(Name = "...")]` | `operationId` (GW004). |
| `[ApiVersion]` (`Deprecated = true`), `[MapToApiVersion]`, `[ApiVersionNeutral]`; `HasApiVersion`, `HasDeprecatedApiVersion`, `MapToApiVersion` calls | One operation per version. A `{version:apiVersion}` route segment gets the version (`/api/v1/pets`). Without one, the operation carries the version (`GET /pets [1.0]`), as in one document per version. GW060 to GW063 then apply. |

### Schemas

| C# | Schema |
|---|---|
| `int`, `long`, `short`, `byte`, `float`, `double`, `decimal`, `bool`, `string`, `char`, `byte[]`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `Guid`, `Uri` | The type and format in the [Microsoft Learn table](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/include-metadata?view=aspnetcore-10.0#include-openapi-metadata-for-data-types). Numbers use the `JsonNumberHandling.Strict` form (`integer`/`int32`), not .NET 10's default `[integer, string]` with a pattern. `TimeSpan` is a string without a format; it is not in the table. |
| Arrays, `List<T>`, `IEnumerable<T>`, `IAsyncEnumerable<T>` and other collections | Arrays. |
| Dictionaries | Objects. |
| Classes, records and structs declared in the project | Object schemas named after the type, so shared-schema grouping works. Generic types are named the way `Microsoft.AspNetCore.OpenApi` names them (`Page<Node>` is `PageOfNode`). Reference cycles are cut. |
| Properties | Public instance properties with a public getter, inherited ones included. The name comes from `[JsonPropertyName]` or the naming policy: camelCase by default, or `PropertyNamingPolicy = null` / `JsonNamingPolicy.SnakeCaseLower` (and the other built-in policies) when set in the JSON options. `[JsonIgnore]` skips a property unless its `Condition` keeps it. |
| Required properties | The `required` modifier, `[Required]`, `[JsonRequired]`, or a matching parameter of the only public constructor (so every positional record parameter is required). |
| Nullable | `T?` for both reference and value types, read from nullable annotations. |
| Enums | Integers, as System.Text.Json writes them. They are strings when a `JsonStringEnumConverter` (or `JsonStringEnumConverter<T>`) is on the enum or the property, or is registered in the JSON options, where the naming policy passed to it also applies. `[JsonStringEnumMemberName]` is respected. |
| `[Range]` (with `MinimumIsExclusive`/`MaximumIsExclusive`), `[MinLength]`, `[MaxLength]`, `[Length]`, `[StringLength]`, `[RegularExpression]` | Bounds and patterns (GW032 to GW035), on properties and on parameters. On arrays, the length attributes are item counts. |
| `[JsonPolymorphic(TypeDiscriminatorPropertyName = ...)]` with `[JsonDerivedType(typeof(Cat), "cat")]` | Variants, each carrying the discriminator property, and a discriminator with its mapping (`$type` by default) for GW030, GW031 and GW037. |

## Limits

- **Source files only.** The files are parsed and compiled together against the running .NET runtime, with no MSBuild, restore or build. One set of preprocessor symbols is used, so `#if` branches for other configurations are not seen. Source generators do not run.
- **ASP.NET Core is recognized by name**, because it is not referenced. A type of your own named `HttpGetAttribute` or `Results` would confuse the reader.
- **Only what the code says statically.** The reader does not see routes built at runtime, endpoints mapped in extension methods whose group prefix comes from the caller, conventions applied by type, filters that change responses, or Asp.Versioning version sets (`NewApiVersionSet()`). A global authorization policy (`RequireAuthorization()` on `MapControllers()`, or a fallback policy) is not applied to each endpoint.
- **JSON options are found anywhere in the project, by syntax.** Controller options and minimal API options are not told apart, and options built for something else, such as an `HttpClient`, are picked up too.
- **Validation libraries are not read.** Only data annotations become constraints, matching what `Microsoft.AspNetCore.OpenApi` documents. FluentValidation and custom validators do not.
- **No `readOnly`/`writeOnly`.** These are not inferred from get-only or init-only properties.

The mapping can get more precise in any release, which can make a finding appear that was always there. The [compatibility policy](compatibility.md) treats that like any rule that detects more, and the release notes list it.
