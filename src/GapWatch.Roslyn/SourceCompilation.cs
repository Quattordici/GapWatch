using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GapWatch.Roslyn;

/// <summary>
/// Compiles a project's <c>.cs</c> files without MSBuild, restore or build: the files are parsed and compiled together
/// against the running .NET runtime, which resolves the project's own types (base classes, property types, constants).
/// ASP.NET Core is not referenced, so its attributes, result types and <c>Map*</c> calls are recognized by name.
/// </summary>
internal static class SourceCompilation
{
    private static readonly string[] ExcludedDirectories = ["bin", "obj", ".git", "node_modules"];

    /// <summary>Every <c>.cs</c> file under <paramref name="directory"/>, skipping <c>bin</c>, <c>obj</c> and <c>.git</c>, ordered by path.</summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public static IEnumerable<(string Path, string Text)> ReadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {directory}");
        }

        return Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .Select(path => (path.Replace('\\', '/'), File.ReadAllText(path)))
            .ToList();
    }

    /// <summary>Compiles <c>(path, text)</c> pairs. The path is used verbatim in locations.</summary>
    public static CSharpCompilation Compile(IEnumerable<(string Path, string Text)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, options, s.Path)).ToList();

        // Projects usually rely on SDK implicit usings, which a bare compilation does not have.
        trees.Add(CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading.Tasks;",
            options,
            ImplicitUsings));

        return CSharpCompilation.Create(
            "GapWatch.Source",
            trees,
            RuntimeReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>The path of the generated tree that holds the implicit usings; it declares nothing to scan.</summary>
    public const string ImplicitUsings = "<implicit-usings>";

    private static readonly Lazy<IReadOnlyList<MetadataReference>> RuntimeReferences = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileName(path) is var name
                           && (name.StartsWith("System.", StringComparison.Ordinal)
                               || name is "System.dll" or "mscorlib.dll" or "netstandard.dll"))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList());
}
