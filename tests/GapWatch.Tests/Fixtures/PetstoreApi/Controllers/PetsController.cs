using Microsoft.AspNetCore.Mvc;

namespace Petstore.Api.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class PetsController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<Pet>> List(
        int? limit,
        string? status,
        [FromQuery(Name = "region")] string area,
        CancellationToken cancellationToken) => Ok();

    [HttpPost]
    public async Task<ActionResult<Pet>> CreateAsync([FromBody] NewPet pet) => Ok();

    public string NotAnEndpoint() => "";
}
