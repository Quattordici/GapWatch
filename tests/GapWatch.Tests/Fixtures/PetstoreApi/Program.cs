using Petstore.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var app = builder.Build();

app.MapControllers();
app.MapGet("/health", () => Results.Ok());

var pets = app.MapGroup(Routes.Pets).WithTags("pets");
pets.MapGet("/{id:long}", GetPet);
pets.MapDelete("/{id}", (long id, CancellationToken cancellationToken) => Results.NoContent());

app.Run();

static IResult GetPet(long id) => Results.Ok();
