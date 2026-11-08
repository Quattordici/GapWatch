using GapWatch.Core;
using GapWatch.Core.Model;

namespace GapWatch.OpenApi;

/// <summary>Picks the right <see cref="IContractReader"/> for a file and loads it.</summary>
public static partial class ContractLoader
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

    /// <summary>
    /// False only for a file that is certainly not an OpenAPI or Swagger document: JSON (comments and trailing commas
    /// allowed, as in <c>appsettings.json</c>) or YAML whose root is a mapping without an <c>openapi</c> or
    /// <c>swagger</c> key. Anything that cannot be parsed counts as a document, so its reader reports the error.
    /// </summary>
    public static bool MayBeOpenApi(string name, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Visual Studio saves appsettings.json with a byte order mark, which the JSON parser does not skip.
                var json = content.AsMemory();
                if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                {
                    json = json[3..];
                }

                using var document = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                return document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                       || document.RootElement.TryGetProperty("openapi", out _)
                       || document.RootElement.TryGetProperty("swagger", out _);
            }
            catch (System.Text.Json.JsonException)
            {
                return true;
            }
        }

        // YAML: look for a top-level key without parsing the whole document.
        var text = System.Text.Encoding.UTF8.GetString(content);
        return TopLevelYamlKey().IsMatch(text) || !AnyTopLevelYamlKey().IsMatch(text);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[""']?(openapi|swagger)[""']?\s*:", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex TopLevelYamlKey();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[""']?[A-Za-z_][\w.-]*[""']?\s*:", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex AnyTopLevelYamlKey();

    /// <summary>True when <paramref name="name"/> has an extension one of the readers supports.</summary>
    public static bool IsSupported(string name) => Readers.Any(r => r.CanRead(name));

    private static string UnsupportedMessage(string path) =>
        $"Unsupported file type '{Path.GetExtension(path).ToLowerInvariant()}' for '{path}'. Supported: .json, .yaml and .yml (OpenAPI 3.x).";
}
