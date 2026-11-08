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

    /// <summary>
    /// Reads an API description from a stream, choosing the reader by <paramref name="name"/>'s extension. Used for
    /// documents that are not files on disk, such as a file at a git revision.
    /// </summary>
    /// <exception cref="ContractReadException">The name has an unsupported extension, or the document is invalid.</exception>
    public static ApiContract Load(string name, Stream content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        var reader = Readers.FirstOrDefault(r => r.CanRead(name))
                     ?? throw new ContractReadException(UnsupportedMessage(name));
        return reader.Read(content);
    }

    /// <summary>True when <paramref name="name"/> has an extension one of the readers supports.</summary>
    public static bool IsSupported(string name) => Readers.Any(r => r.CanRead(name));

    private static string UnsupportedMessage(string path) =>
        $"Unsupported file type '{Path.GetExtension(path).ToLowerInvariant()}' for '{path}'. Supported: .json, .yaml and .yml (OpenAPI 3.x).";
}
