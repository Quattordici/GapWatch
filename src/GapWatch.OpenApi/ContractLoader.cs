using GapWatch.Core;
using GapWatch.Core.Model;

namespace GapWatch.OpenApi;

/// <summary>Picks the right <see cref="IContractReader"/> for a file and loads it.</summary>
public static class ContractLoader
{
    private static readonly IContractReader[] Readers = [new OpenApiJsonReader(), new OpenApiYamlReader()];

    /// <summary>Loads and normalizes an API description from disk.</summary>
    /// <exception cref="ContractReadException">The file is missing, unsupported or invalid.</exception>
    public static ApiContract Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new ContractReadException($"File not found: {path}");
        }

        var reader = Readers.FirstOrDefault(r => r.CanRead(path))
                     ?? throw new ContractReadException(UnsupportedMessage(path));

        try
        {
            using var stream = File.OpenRead(path);
            return reader.Read(stream);
        }
        catch (IOException ex)
        {
            throw new ContractReadException($"Could not read '{path}': {ex.Message}", ex);
        }
    }

    private static string UnsupportedMessage(string path) =>
        $"Unsupported file type '{Path.GetExtension(path).ToLowerInvariant()}' for '{path}'. Supported: .json, .yaml and .yml (OpenAPI 3.x).";
}
