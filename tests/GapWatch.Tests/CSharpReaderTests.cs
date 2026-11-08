using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Model;
using GapWatch.Roslyn;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>Building the contract from ASP.NET Core source: operations, parameters, bodies, responses and security.</summary>
public sealed class CSharpReaderTests
{
    private const string Usings = """
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;

        public sealed record Pet(long Id, string Name);
        public sealed record NewPet(string Name);
        public interface IPetStore { }

        """;

    private static ApiContract Read(string source) => CSharpContractReader.FromSources([("Api.cs", Usings + source)]);

    private static OperationContract Operation(string source, string display) =>
        Read(source).Operations.Single(o => o.Display == display);

    private static string Operations(ApiContract contract) =>
        string.Join(", ", contract.Operations.Select(o => o.Display).Order(StringComparer.Ordinal));

    private static string Parameters(OperationContract operation) =>
        string.Join(", ", operation.Parameters.Select(p => $"{p.Name} {p.Location}{(p.Required ? "!" : "")} {Schema(p.Schema)}"));

    private static string Body(OperationContract operation) =>
        operation.RequestBody is { } body
            ? $"{(body.Required ? "required" : "optional")} " + string.Join(", ", body.Content.Select(c => $"{c.Key} {Schema(c.Value)}"))
            : "none";

    private static string Responses(OperationContract operation) =>
        string.Join(", ", operation.Responses.Select(r =>
            r.Content.Count == 0 ? r.StatusCode : $"{r.StatusCode} " + string.Join(" ", r.Content.Select(c => $"{c.Key} {Schema(c.Value)}"))));

    private static string Schema(SchemaContract schema) =>
        (schema.ReferenceName
         ?? (schema.Items is { } items ? $"{Schema(items)}[]" : schema.Types.Count == 0 ? "any" : string.Join("|", schema.Types) + (schema.Format is null ? "" : $"/{schema.Format}")))
        + (schema.Nullable ? "?" : "");

    private static ComparisonResult Diff(string before, string after) =>
        new ContractComparer().Compare(Read(before), Read(after));

    // --- Controllers ---------------------------------------------------------------------------------------------

    private const string PetsController = """
        [ApiController]
        [Route("api/[controller]")]
        public sealed class PetsController : ControllerBase
        {
            [HttpGet("{id:long}")]
            public ActionResult<Pet> Get(long id) => Ok();

            [HttpGet("{id}/photos/{index:int}")]
            public IActionResult Photo(long id) => Ok();

            [HttpGet]
            public ActionResult<IReadOnlyList<Pet>> List(
                int? limit,
                string? status,
                [FromQuery(Name = "region")] string area,
                [FromHeader(Name = "X-Tenant")] Guid tenant,
                int page = 1,
                [FromServices] IPetStore store = null!,
                CancellationToken cancellationToken = default) => Ok();

            [HttpPost]
            public async Task<ActionResult<Pet>> CreateAsync(NewPet pet, IPetStore store) => Ok();

            [HttpDelete("{id}")]
            public Task Delete(long id) => Task.CompletedTask;

            [HttpGet("{id}/name")]
            public string Name(long id) => "";
        }
        """;

    [Test]
    public void Controller_routes_become_openapi_paths_with_typed_path_parameters()
    {
        var contract = Read(PetsController);

        Operations(contract).ShouldBe(
            "DELETE /api/Pets/{id}, GET /api/Pets, GET /api/Pets/{id}, GET /api/Pets/{id}/name, GET /api/Pets/{id}/photos/{index}, POST /api/Pets");
        Parameters(Operation(PetsController, "GET /api/Pets/{id}")).ShouldBe("id path! integer/int64");
        Parameters(Operation(PetsController, "GET /api/Pets/{id}/photos/{index}")).ShouldBe("id path! integer/int64, index path! integer/int32");
    }

    [Test]
    public void Simple_types_bind_from_the_query_and_services_and_framework_types_are_not_parameters()
    {
        Parameters(Operation(PetsController, "GET /api/Pets")).ShouldBe(
            "limit query integer/int32?, status query string?, region query! string, X-Tenant header! string/uuid, page query integer/int32");
    }

    [Test]
    public void A_project_type_is_the_body_of_an_api_controller_action_and_required_unless_nullable()
    {
        Body(Operation(PetsController, "POST /api/Pets")).ShouldBe("required application/json NewPet");

        var optional = Operation(PetsController.Replace("NewPet pet,", "[FromBody] NewPet? pet,", StringComparison.Ordinal), "POST /api/Pets");
        Body(optional).ShouldBe("optional application/json NewPet?");

        // Without [ApiController], MVC binds a complex type from the form and query, not the body.
        var plain = Operation(PetsController.Replace("[ApiController]", "", StringComparison.Ordinal), "POST /api/Pets");
        Body(plain).ShouldBe("none");
    }

    [Test]
    public void Required_follows_nullability_defaults_and_the_required_attribute()
    {
        var operation = Operation("""
            public sealed class SearchController : ControllerBase
            {
                [HttpGet("search")]
                public IActionResult Search(int count, int? limit, string term, string? sort, [System.ComponentModel.DataAnnotations.Required] string? owner, bool all = false) => Ok();
            }
            """, "GET /search");

        Parameters(operation).ShouldBe(
            "count query! integer/int32, limit query integer/int32?, term query! string, sort query string?, owner query! string?, all query boolean");
    }

    [Test]
    public void The_return_type_gives_the_success_response()
    {
        Responses(Operation(PetsController, "GET /api/Pets/{id}")).ShouldBe("200 application/json Pet");
        Responses(Operation(PetsController, "GET /api/Pets")).ShouldBe("200 application/json Pet[]");
        Responses(Operation(PetsController, "POST /api/Pets")).ShouldBe("200 application/json Pet");
        Responses(Operation(PetsController, "GET /api/Pets/{id}/photos/{index}")).ShouldBe("200");
        Responses(Operation(PetsController, "DELETE /api/Pets/{id}")).ShouldBe("200");
        Responses(Operation(PetsController, "GET /api/Pets/{id}/name")).ShouldBe("200 text/plain string");
    }

    [Test]
    public void ProducesResponseType_declares_responses_and_a_typeless_success_takes_the_return_type()
    {
        var operation = Operation("""
            [ApiController]
            [Route("pets")]
            [ProducesResponseType(StatusCodes.Status401Unauthorized)]
            public sealed class PetsController : ControllerBase
            {
                [HttpGet("{id}")]
                [ProducesResponseType(StatusCodes.Status200OK)]
                [ProducesResponseType<ProblemDetails>(404)]
                [ProducesResponseType(typeof(Pet), (int)System.Net.HttpStatusCode.Accepted, "application/xml")]
                public ActionResult<Pet> Get(long id) => Ok();
            }
            """, "GET /pets/{id}");

        Responses(operation).ShouldBe("200 application/json Pet, 202 application/xml Pet, 401, 404 application/json ProblemDetails");
    }

    [Test]
    public void A_declared_success_replaces_the_inferred_one_and_Produces_sets_the_media_types()
    {
        var operation = Operation("""
            [ApiController]
            [Produces("application/json", "application/xml")]
            public sealed class PetsController : ControllerBase
            {
                [HttpPost("pets")]
                [ProducesResponseType(typeof(Pet), 201)]
                [ProducesResponseType(400)]
                public ActionResult<Pet> Create(NewPet pet) => Ok();
            }
            """, "POST /pets");

        Responses(operation).ShouldBe("201 application/json Pet application/xml Pet, 400");
    }

    [Test]
    public void Authorize_and_AllowAnonymous_give_the_security_alternative()
    {
        const string Source = """
            [ApiController]
            [Authorize]
            [Route("pets")]
            public sealed class PetsController : ControllerBase
            {
                [HttpGet] public IActionResult List() => Ok();
                [HttpGet("public"), AllowAnonymous] public IActionResult Public() => Ok();
                [HttpDelete("{id}"), Authorize(Policy = "Admin"), Authorize(Roles = "Ops, Owner")] public IActionResult Delete(long id) => Ok();
            }
            public sealed class OpenController : ControllerBase
            {
                [HttpGet("open")] public IActionResult Get() => Ok();
            }
            """;

        static string Security(string display) => string.Join(" OR ", Operation(Source, display).Security);
        Security("GET /pets").ShouldBe("authenticated[]");
        Security("GET /pets/public").ShouldBe("");
        Security("DELETE /pets/{id}").ShouldBe("authenticated[]+policy[Admin]+roles[Ops,Owner]");
        Security("GET /open").ShouldBe("");
    }

    [Test]
    public void Obsolete_marks_deprecated_and_the_route_name_is_the_operation_id()
    {
        var operation = Operation("""
            [ApiController]
            public sealed class PetsController : ControllerBase
            {
                [Obsolete("Use v2")]
                [HttpGet("pets/{id}", Name = "GetPet")]
                public ActionResult<Pet> Get(long id) => Ok();
            }
            """, "GET /pets/{id}");

        operation.Deprecated.ShouldBeTrue();
        operation.OperationId.ShouldBe("GetPet");
    }

    // --- Minimal APIs --------------------------------------------------------------------------------------------

    private const string MinimalApi = """
        public static class Endpoints
        {
            public static void Map(WebApplication app)
            {
                var pets = app.MapGroup("/pets").WithTags("pets");
                pets.RequireAuthorization("Reader");

                pets.MapGet("/{id:long}", (long id) => TypedResults.Ok(new Pet(id, "Rex")))
                    .WithName("GetPet")
                    .Produces(404);
                pets.MapGet("/", async (int? limit, IPetStore store) =>
                {
                    await Task.Yield();
                    return new List<Pet>();
                }).AllowAnonymous();
                pets.MapPost("/", (NewPet pet) => TypedResults.Created("/pets/1", new Pet(1, pet.Name)))
                    .ProducesProblem(500);
                pets.MapDelete("/{id}", Delete);
                pets.MapPut("/{id}/photo", (long id, IFormFile photo, string? caption) => Results.NoContent());
                app.MapGet("/search", (Pet filter) => Results.Ok());
            }

            private static Results<NoContent, NotFound> Delete(long id) => TypedResults.NoContent();
        }
        """;

    [Test]
    public void Map_calls_and_groups_become_operations()
    {
        Operations(Read(MinimalApi)).ShouldBe(
            "DELETE /pets/{id}, GET /pets, GET /pets/{id}, GET /search, POST /pets, PUT /pets/{id}/photo");
    }

    [Test]
    public void Minimal_api_responses_come_from_typed_results_and_produces_calls()
    {
        Responses(Operation(MinimalApi, "GET /pets/{id}")).ShouldBe("200 application/json Pet, 404");
        Responses(Operation(MinimalApi, "GET /pets")).ShouldBe("200 application/json Pet[]");
        Responses(Operation(MinimalApi, "POST /pets")).ShouldBe("201 application/json Pet, 500 application/problem+json ProblemDetails");
        Responses(Operation(MinimalApi, "DELETE /pets/{id}")).ShouldBe("204, 404");
        Responses(Operation(MinimalApi, "GET /search")).ShouldBe("200");
    }

    [Test]
    public void Minimal_api_binding_infers_the_body_only_where_the_method_allows_one()
    {
        Body(Operation(MinimalApi, "POST /pets")).ShouldBe("required application/json NewPet");
        Body(Operation(MinimalApi, "GET /search")).ShouldBe("none");
        Parameters(Operation(MinimalApi, "GET /pets")).ShouldBe("limit query integer/int32?");
        Body(Operation(MinimalApi, "PUT /pets/{id}/photo")).ShouldBe("required multipart/form-data object");
    }

    [Test]
    public void Group_and_endpoint_conventions_give_security_and_the_operation_id()
    {
        Operation(MinimalApi, "GET /pets/{id}").Security.ShouldBe(["policy[Reader]"]);
        Operation(MinimalApi, "GET /pets").Security.ShouldBeEmpty();
        Operation(MinimalApi, "GET /pets/{id}").OperationId.ShouldBe("GetPet");
    }

    // --- Comparing two versions of the code ----------------------------------------------------------------------

    [Test]
    public void A_parameter_that_becomes_required_breaks_requests_and_the_reverse_is_safe()
    {
        const string Optional = """public sealed class C : ControllerBase { [HttpGet("pets")] public IActionResult List(int? limit) => Ok(); }""";
        var required = Optional.Replace("int? limit", "int limit", StringComparison.Ordinal);

        // int? is also nullable, as generators document it, so null is no longer accepted either.
        Diff(Optional, required).RuleIds().ShouldBe("GW011,GW022");
        Diff(required, Optional).RuleIds().ShouldBe(string.Empty);
    }

    [Test]
    public void Requiring_authorization_breaks_callers_and_allowing_anonymous_access_does_not()
    {
        const string Open = """public sealed class C : ControllerBase { [HttpGet("pets")] public IActionResult List() => Ok(); }""";
        var secured = Open.Replace("[HttpGet", "[Authorize, HttpGet", StringComparison.Ordinal);

        Diff(Open, secured).RuleIds().ShouldBe("GW050");
        Diff(secured, Open).RuleIds().ShouldBe(string.Empty);
    }

    [Test]
    public void Removing_an_endpoint_and_a_response_status_are_reported_with_the_document_strings()
    {
        var before = MinimalApi;
        var after = MinimalApi
            .Replace("""pets.MapDelete("/{id}", Delete);""", "", StringComparison.Ordinal)
            .Replace(".Produces(404);", ";", StringComparison.Ordinal);

        var result = Diff(before, after);
        string.Join(" | ", result.Findings.Select(f => $"{f.Rule.Id} {f.Operation} {f.Location}"))
            .ShouldBe("GW001 DELETE /pets/{id} operation | GW045 GET /pets/{id} response 404");
        Diff(after, before).Findings.Select(f => f.Rule.Id).ShouldNotContain("GW001");
    }

    [Test]
    public void Findings_point_at_the_file_and_line_in_the_code()
    {
        const string Before = """
            public sealed class C : ControllerBase
            {
                [HttpGet("pets")]
                public IActionResult List(int? limit) => Ok();
            }
            """;

        var finding = Diff(Before, Before.Replace("int? limit", "string limit", StringComparison.Ordinal)).Findings.First();
        finding.Document.ShouldBe("Api.cs");
        finding.Position.ShouldBe(new SourcePosition(11, 38) { File = "Api.cs" });
    }

    // --- API versions (Asp.Versioning) ---------------------------------------------------------------------------

    private const string Versioned = """
        [ApiController]
        [ApiVersion("1.0", Deprecated = true)]
        [ApiVersion("2.0")]
        [Route("api/v{version:apiVersion}/pets")]
        public sealed class PetsController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpGet("{id}"), MapToApiVersion("2.0")] public IActionResult Get(long id) => Ok();
        }

        [ApiController]
        [ApiVersion(3.0)]
        [Route("owners")]
        public sealed class OwnersController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
        }
        """;

    [Test]
    public void Api_versions_expand_into_the_path_or_onto_the_operation()
    {
        var contract = Read(Versioned);

        Operations(contract).ShouldBe("GET /api/v1/pets, GET /api/v2/pets, GET /api/v2/pets/{id}, GET /owners [3.0]");
        string.Join(", ", contract.Operations.Where(o => o.Deprecated).Select(o => o.Display)).ShouldBe("GET /api/v1/pets");
        Parameters(contract.Operations.Single(o => o.Display == "GET /api/v2/pets/{id}")).ShouldBe("id path! integer/int64");
    }

    [Test]
    public void Minimal_api_versions_come_from_the_endpoint_conventions()
    {
        var contract = Read("""
            public static class Endpoints
            {
                public static void Map(WebApplication app) =>
                    app.MapGet("/v{version:apiVersion}/pets", () => Results.Ok()).HasApiVersion(new ApiVersion(1, 0)).HasDeprecatedApiVersion(0.9);
            }
            """);

        Operations(contract).ShouldBe("GET /v0.9/pets, GET /v1/pets");
        contract.Operations.Single(o => o.Path == "/v0.9/pets").Deprecated.ShouldBeTrue();
    }

    private static string VersionDiff(string before, string after)
    {
        var (was, now) = (Read(before), Read(after));
        var result = ApiVersionAnalyzer.Apply(was, now, new ContractComparer().Compare(was, now), VersioningOptions.Default);
        return string.Join(",", result.Findings.Select(f => f.Rule.Id).Order(StringComparer.Ordinal));
    }

    [Test]
    public void Removing_a_version_attribute_retires_the_version_and_adding_one_is_info()
    {
        var withoutV1 = Versioned.Replace("""[ApiVersion("1.0", Deprecated = true)]""", "", StringComparison.Ordinal);
        var v1Live = Versioned.Replace(", Deprecated = true", "", StringComparison.Ordinal);

        VersionDiff(Versioned, withoutV1).ShouldBe("GW061");
        VersionDiff(v1Live, withoutV1).ShouldBe("GW060");
        VersionDiff(withoutV1, Versioned).ShouldBe("GW062");
        VersionDiff(v1Live, Versioned).ShouldBe("GW063");
    }

    [Test]
    public void The_fixture_project_reads_without_errors()
    {
        var contract = CSharpContractReader.Load(TestSpec.FixturePath("PetstoreApi"));

        Operations(contract).ShouldBe("DELETE /pets/{id}, GET /Pets, GET /health, GET /pets/{id}, POST /Pets");
    }
}
