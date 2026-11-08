using GapWatch.Core.Model;

namespace GapWatch.Roslyn;

/// <summary>
/// Builds an <see cref="ApiContract"/> straight from ASP.NET Core source: controller actions and minimal API endpoints,
/// their parameters, request bodies, responses, security and deprecation. Every rule then applies as it does to an
/// OpenAPI document, and operation and location strings have the same form, so suppressions and baselines carry over.
/// </summary>
/// <remarks>
/// Like <see cref="CodeMap"/>, it reads source files only: no MSBuild, restore or build. What it cannot see statically
/// (routes built at runtime, filters, conventions applied by type, source generators) is not in the contract. Comparing
/// two versions of the same code with the same reader is consistent even where the reader differs from a generator:
/// a detail it misses is missed on both sides.
/// </remarks>
public static class CSharpContractReader
{
    /// <summary>Reads every <c>.cs</c> file under <paramref name="directory"/>, skipping <c>bin</c>, <c>obj</c> and <c>.git</c>.</summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public static ApiContract Load(string directory) => FromSources(SourceCompilation.ReadDirectory(directory));

    /// <summary>Reads in-memory sources: <c>(path, text)</c> pairs. The path is used verbatim in positions.</summary>
    public static ApiContract FromSources(IEnumerable<(string Path, string Text)> sources)
    {
        var compilation = SourceCompilation.Compile(sources);
        var reader = new OperationReader(compilation, new SchemaMapper(compilation, JsonConventions.Find(compilation)));

        var operations = compilation.SyntaxTrees
            .SelectMany(tree => EndpointScanner.Scan(tree, compilation))
            .SelectMany(reader.ReadAll)
            .ToList();

        return new ApiContract(null, null, operations);
    }
}
