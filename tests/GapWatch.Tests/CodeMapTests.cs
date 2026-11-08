using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.OpenApi;
using GapWatch.Roslyn;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>Mapping findings to the C# code behind the head contract.</summary>
public sealed class CodeMapTests
{
    private static readonly string ApiDirectory = TestSpec.FixturePath("PetstoreApi");

    private static CodeMap Petstore() => CodeMap.Load(ApiDirectory);

    private static string File(string name) => Path.Join(ApiDirectory, name).Replace('\\', '/');

    private static CodeMap Single(string source) => CodeMap.FromSources([("Api.cs", source)]);

    private static string Routes(CodeMap map) =>
        string.Join(", ", map.Endpoints.Select(e => $"{e.Method} {e.Route}").Order(StringComparer.Ordinal));

    private static Finding At(string operation, FindingLocation? location = null, SchemaOrigin? origin = null) =>
        new(RuleCatalog.TypeChanged, operation, location ?? FindingLocation.Operation.Instance, "m") { Origin = origin };

    private static FindingLocation Query(string name) => new FindingLocation.Parameter(name, "query");

    private static FindingLocation PathParameter(string name) => new FindingLocation.Parameter(name, "path");

    [Test]
    public void Controller_actions_and_minimal_api_endpoints_are_found_with_their_routes()
    {
        Routes(Petstore()).ShouldBe("DELETE /pets/{id}, GET /health, GET /pets/{id:long}, GET Pets, POST Pets");
    }

    [Test]
    public void An_operation_maps_to_its_action_or_its_map_call()
    {
        var map = Petstore();

        map.Locate(At("POST /pets")).ShouldBe(new CodeLocation(File("Controllers/PetsController.cs"), 17, 42));
        map.Locate(At("GET /pets/{petId}")).ShouldBe(new CodeLocation(File("Program.cs"), 11, 6));
        map.Locate(At("GET /unknown")).ShouldBeNull();
        map.Locate(At("POST /pets [2.0]")).ShouldBe(new CodeLocation(File("Controllers/PetsController.cs"), 17, 42));
    }

    [Test]
    public void A_parameter_maps_to_the_bound_parameter_including_a_renamed_one()
    {
        var map = Petstore();

        map.Locate(At("GET /pets", Query("status"))).ShouldBe(new CodeLocation(File("Controllers/PetsController.cs"), 12, 17));
        map.Locate(At("GET /pets", Query("region"))).ShouldBe(new CodeLocation(File("Controllers/PetsController.cs"), 13, 45));
        map.Locate(At("GET /pets/{id}", PathParameter("id"))).ShouldBe(new CodeLocation(File("Program.cs"), 16, 28));
        map.Locate(At("DELETE /pets/{id}", PathParameter("id"))).ShouldBe(new CodeLocation(File("Program.cs"), 12, 31));
    }

    [Test]
    public void A_component_change_maps_to_the_dto_property_following_json_names_and_base_classes()
    {
        var map = Petstore();
        CodeLocation? Schema(string component, string path) =>
            map.Locate(At("GET /pets", origin: new SchemaOrigin(component, path, Direction.Response)));

        Schema("Pet", "$").ShouldBe(new CodeLocation(File("Models.cs"), 15, 22));
        Schema("Pet", "$.name").ShouldBe(new CodeLocation(File("Models.cs"), 17, 13));
        Schema("Pet", "$.createdAt").ShouldBe(new CodeLocation(File("Models.cs"), 21, 62));
        Schema("Pet", "$.owner.id").ShouldBe(new CodeLocation(File("Models.cs"), 12, 17));
        Schema("Category", "$.parent.parent.name").ShouldBe(new CodeLocation(File("Models.cs"), 42, 20));
    }

    [Test]
    public void A_property_that_is_not_in_the_code_maps_to_its_type_and_ignored_properties_do_not_match()
    {
        var map = Petstore();

        map.Locate(At("POST /pets", origin: new SchemaOrigin("NewPet", "$.tag", Direction.Request)))
            .ShouldBe(new CodeLocation(File("Models.cs"), 23, 21));
        map.Locate(At("POST /pets", origin: new SchemaOrigin("NewPet", "$.internal", Direction.Request)))
            .ShouldBe(new CodeLocation(File("Models.cs"), 23, 21));
    }

    [Test]
    public void Build_output_is_skipped_so_it_cannot_make_type_names_ambiguous()
    {
        Petstore().Locate(At("GET /pets", origin: new SchemaOrigin("Pet", "$", Direction.Response))).ShouldNotBeNull();
    }

    [Test]
    public void Ambiguous_type_names_and_removed_operations_have_no_code_location()
    {
        var map = CodeMap.FromSources([("A.cs", "namespace A { public class Pet; }"), ("B.cs", "namespace B { public class Pet; }")]);

        map.Locate(At("GET /pets", origin: new SchemaOrigin("Pet", "$", Direction.Response))).ShouldBeNull();
        Petstore().Locate(new Finding(RuleCatalog.OperationRemoved, "GET /pets", "operation", "m")).ShouldBeNull();
    }

    [Test]
    public void Route_templates_combine_like_attribute_routing()
    {
        var map = Single("""
            [Route("api/v1/[controller]")]
            public abstract class ApiBase : ControllerBase;

            public class OrdersController : ApiBase
            {
                [HttpGet("{id:int?}")] public object Get(int id) => null!;
                [HttpGet("/status")] public object Status() => null!;
                [HttpPut, Route("[action]/{code=none}")] public object Rename(string code) => null!;
                [HttpGet("items/{**rest}"), ActionName("ignored")] public object Items(string rest) => null!;
                [NonAction] [HttpGet("x")] public object Hidden() => null!;
                private object Private() => null!;
            }

            [NonController]
            public class HelperController { [HttpGet("/nope")] public object Get() => null!; }
            """);

        Routes(map).ShouldBe("GET /status, GET api/v1/Orders/items/{**rest}, GET api/v1/Orders/{id:int?}, PUT api/v1/Orders/Rename/{code=none}");
        map.Locate(At("GET /api/v1/orders/{orderId}")).ShouldNotBeNull();
        map.Locate(At("GET /api/v1/orders/items/{rest}")).ShouldNotBeNull();
        map.Locate(At("PUT /api/v1/orders/rename/{code}")).ShouldNotBeNull();
    }

    [Test]
    public void Nested_and_fluent_map_groups_contribute_their_prefixes()
    {
        var map = Single("""
            var api = app.MapGroup("/api").RequireAuthorization();
            var v1 = api.MapGroup("v1");
            v1.MapGroup("/orders").WithTags("orders").MapPost("/", (Order order) => 1);
            v1.MapGet("{id}", (int id) => 1);
            app.MapGet(Paths.Root, () => 1);
            app.MapGet(PathFromSomewhere(), () => 1);

            static class Paths { public const string Root = "/"; }
            """);

        Routes(map).ShouldBe("GET /, GET /api/v1/{id}, POST /api/v1/orders/");
    }

    [Test]
    public void Annotate_adds_code_locations_to_active_and_suppressed_findings()
    {
        var result = new ContractComparer().Compare(
            ContractLoader.Load(TestSpec.FixturePath("petstore-v1.json")),
            ContractLoader.Load(TestSpec.FixturePath("petstore-v2.json")));

        var annotated = Petstore().Annotate(result);

        annotated.Findings.Count.ShouldBe(result.Findings.Count);
        annotated.Findings.Single(f => f.Rule == RuleCatalog.RequiredParameterAdded).Code
            .ShouldBe(new CodeLocation(File("Controllers/PetsController.cs"), 13, 45));
        annotated.Findings.Where(f => f.Rule == RuleCatalog.TypeChanged).Select(f => f.Code).Distinct()
            .ShouldHaveSingleItem().ShouldBe(new CodeLocation(File("Models.cs"), 16, 12));
        annotated.Findings.Single(f => f.Rule == RuleCatalog.OperationRemoved).Code.ShouldBeNull();
    }
}
