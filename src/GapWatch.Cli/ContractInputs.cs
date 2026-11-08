using System.Text;
using GapWatch.Core;
using GapWatch.Core.Comparison;
using GapWatch.Core.Model;
using GapWatch.OpenApi;
using GapWatch.Roslyn;

namespace GapWatch.Cli;

/// <summary>What <c>&lt;base&gt;</c> and <c>&lt;head&gt;</c> are (<c>--input</c>).</summary>
public enum InputKind
{
    /// <summary>OpenAPI documents: two files, or two folders with one document per API version.</summary>
    OpenApi,

    /// <summary>Two folders of ASP.NET Core source; the contract is read from the code.</summary>
    CSharp,
}

/// <summary>The two contracts of a comparison, and where findings without a head document point.</summary>
/// <param name="Base">What consumers rely on today.</param>
/// <param name="Head">The proposed change.</param>
/// <param name="FallbackHeadDocument">In folder mode, the first head document; null otherwise.</param>
public sealed record ComparisonInputs(ApiContract Base, ApiContract Head, string? FallbackHeadDocument)
{
    /// <summary>
    /// In folder mode, the JSON and YAML files that are not OpenAPI documents (such as <c>appsettings.json</c> next to a
    /// generated document) and were left out, by label.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}

/// <summary>The input is not of a shape <c>compare</c> accepts, such as a file on one side and a folder on the other.</summary>
public sealed class InputException(string message) : Exception(message);

/// <summary>
/// Reads both sides of a comparison, each from its own <see cref="ISourceTree"/> (the working tree, or a git revision
/// for <c>--base-ref</c>). Owns the input rules: two files or two folders, folders paired by API version, and C# folders.
/// </summary>
public static class ContractInputs
{
    /// <summary>Reads <paramref name="basePath"/> from <paramref name="baseTree"/> and <paramref name="headPath"/> from <paramref name="headTree"/>.</summary>
    /// <exception cref="InputException">The inputs are not two files or two folders, or not folders for C#.</exception>
    /// <exception cref="ContractReadException">A path is missing, or a document cannot be read.</exception>
    public static ComparisonInputs Load(ISourceTree baseTree, string basePath, ISourceTree headTree, string headPath, InputKind kind)
    {
        ArgumentNullException.ThrowIfNull(baseTree);
        ArgumentNullException.ThrowIfNull(headTree);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(headPath);

        return kind == InputKind.CSharp
            ? LoadSources(baseTree, basePath, headTree, headPath)
            : LoadDocuments(baseTree, basePath, headTree, headPath);
    }

    private static ComparisonInputs LoadDocuments(ISourceTree baseTree, string basePath, ISourceTree headTree, string headPath)
    {
        var baseKind = Existing(baseTree, basePath);
        var headKind = Existing(headTree, headPath);
        if (baseKind != headKind)
        {
            throw new InputException("compare needs two files, or two folders with one document per API version, not one of each.");
        }

        if (baseKind == EntryKind.File)
        {
            return new ComparisonInputs(Document(baseTree.Read(basePath)), Document(headTree.Read(headPath)), null);
        }

        var skipped = new List<string>();
        var headDocuments = Documents(headTree, headPath, skipped);
        var (combinedBase, combinedHead) = VersionedDocuments.Combine(Documents(baseTree, basePath, skipped), headDocuments);
        return new ComparisonInputs(combinedBase, combinedHead, headDocuments[0].Path) { Skipped = skipped };
    }

    private static ComparisonInputs LoadSources(ISourceTree baseTree, string basePath, ISourceTree headTree, string headPath)
    {
        if (baseTree.Kind(basePath) != EntryKind.Folder || headTree.Kind(headPath) != EntryKind.Folder)
        {
            throw new InputException("--input csharp needs <base> and <head> to be folders of C# source.");
        }

        return new ComparisonInputs(Sources(baseTree, basePath), Sources(headTree, headPath), null);
    }

    private static EntryKind Existing(ISourceTree tree, string path) =>
        tree.Kind(path) is not EntryKind.Missing and var kind ? kind : throw new ContractReadException($"File not found: {path}");

    /// <summary>Reads one document; a read error names the file, so a folder with several says which one failed.</summary>
    private static ApiContract Document(SourceFile file)
    {
        using var content = new MemoryStream(file.Content, writable: false);
        try
        {
            return ContractLoader.Load(file.Name, content);
        }
        catch (ContractReadException ex) when (!ex.Message.Contains(file.Label, StringComparison.Ordinal))
        {
            throw new ContractReadException($"{file.Label}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Every OpenAPI document directly in the folder (<c>.json</c>, <c>.yaml</c>, <c>.yml</c>), ordered by file name.
    /// Files that are not OpenAPI documents at all are added to <paramref name="skipped"/> instead.
    /// </summary>
    private static IReadOnlyList<NamedContract> Documents(ISourceTree tree, string folder, List<string> skipped)
    {
        var files = tree.ReadFolder(folder, recursive: false, ContractLoader.IsSupported);
        skipped.AddRange(files.Where(f => !ContractLoader.MayBeOpenApi(f.Name, f.Content)).Select(f => f.Label));
        var documents = files
            .Where(f => ContractLoader.MayBeOpenApi(f.Name, f.Content))
            .Select(file => new NamedContract(file.Label, Document(file)))
            .ToList();

        return documents.Count > 0
            ? documents
            : throw new ContractReadException($"No OpenAPI documents (.json, .yaml, .yml) in folder: {folder}");
    }

    private static ApiContract Sources(ISourceTree tree, string folder) =>
        CSharpContractReader.FromSources(
            tree.ReadFolder(folder, recursive: true, CSharpContractReader.IsSourceFile)
                .Select(file => (file.Label, Decode(file.Content)))
                .ToList(),
            ProjectSettings.FindNullable(folder, directory => ProjectFiles(tree, directory)));

    /// <summary>The <c>.csproj</c> and <c>Directory.Build.props</c> files directly in a folder of the tree, if it has it.</summary>
    private static IReadOnlyList<(string Name, string Text)> ProjectFiles(ISourceTree tree, string directory)
    {
        try
        {
            return tree.Kind(directory) == EntryKind.Folder
                ? tree.ReadFolder(directory, recursive: false, ProjectSettings.IsProjectFile)
                    .Select(file => (file.Name, Decode(file.Content)))
                    .ToList()
                : [];
        }
        catch (ContractReadException)
        {
            // Outside the repository, or missing at the revision: nothing to read there.
            return [];
        }
    }

    /// <summary>Decodes source text the way <see cref="File.ReadAllText(string)"/> does: UTF-8 unless a byte order mark says otherwise.</summary>
    private static string Decode(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
