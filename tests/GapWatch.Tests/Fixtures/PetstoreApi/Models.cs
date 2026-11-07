using System.Text.Json.Serialization;

namespace Petstore.Api;

public static class Routes
{
    public const string Pets = "/pets";
}

public abstract class Entity
{
    public long Id { get; set; }
}

public sealed record Pet(
    string Id,
    string? Name,
    string Status,
    Owner? Owner,
    Category? Category,
    [property: JsonPropertyName("createdAt")] DateTimeOffset Created);

public sealed class NewPet
{
    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required long OwnerId { get; init; }

    [JsonIgnore]
    public string? Internal { get; init; }
}

public sealed class Owner : Entity
{
    public string? Name { get; set; }
}

public sealed class Category
{
    public string? Name { get; set; }

    public Category? Parent { get; set; }
}
