namespace GapWatch.Cli;

/// <summary>What a path names in a <see cref="ISourceTree"/>.</summary>
public enum EntryKind
{
    /// <summary>Nothing at that path.</summary>
    Missing,

    /// <summary>A file.</summary>
    File,

    /// <summary>A folder.</summary>
    Folder,
}

/// <summary>One file read from a <see cref="ISourceTree"/>.</summary>
/// <param name="Label">The file as reports show it: <c>api/v1.json</c>, or <c>origin/main:api/v1.json</c> at a revision.</param>
/// <param name="Name">The file name, which picks the reader by its extension.</param>
/// <param name="Content">The raw bytes.</param>
public sealed record SourceFile(string Label, string Name, byte[] Content);

/// <summary>
/// Where the inputs of a comparison are read from: the working tree, or a git revision (<c>--base-ref</c>). Paths are
/// as the user gave them, relative to the current directory or absolute, so both sides of a comparison can use the same
/// path. Only reads; nothing is written.
/// </summary>
public interface ISourceTree
{
    /// <summary>Whether <paramref name="path"/> is a file, a folder, or missing.</summary>
    EntryKind Kind(string path);

    /// <summary>Reads one file.</summary>
    SourceFile Read(string path);

    /// <summary>
    /// Reads the files in <paramref name="folder"/>, directly in it or, with <paramref name="recursive"/>, in any folder
    /// below it, whose path relative to the folder (with <c>/</c> separators) passes <paramref name="include"/>. Ordered
    /// by that relative path, ordinal. One call, so an implementation can read them in bulk.
    /// </summary>
    IReadOnlyList<SourceFile> ReadFolder(string folder, bool recursive, Func<string, bool> include);
}

/// <summary>The working tree: the file system as it is now.</summary>
public sealed class WorkingTree : ISourceTree
{
    /// <summary>The single instance.</summary>
    public static WorkingTree Instance { get; } = new();

    private WorkingTree()
    {
    }

    /// <inheritdoc />
    public EntryKind Kind(string path) =>
        Directory.Exists(path) ? EntryKind.Folder : File.Exists(path) ? EntryKind.File : EntryKind.Missing;

    /// <inheritdoc />
    public SourceFile Read(string path) => new(Label(path), Path.GetFileName(path), File.ReadAllBytes(path));

    /// <inheritdoc />
    public IReadOnlyList<SourceFile> ReadFolder(string folder, bool recursive, Func<string, bool> include)
    {
        ArgumentNullException.ThrowIfNull(include);

        var label = Label(folder).TrimEnd('/');
        return Directory
            .EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(folder, path).Replace('\\', '/')))
            .Where(f => include(f.Relative))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .Select(f => new SourceFile($"{label}/{f.Relative}", Path.GetFileName(f.Path), File.ReadAllBytes(f.Path)))
            .ToList();
    }

    private static string Label(string path) => path.Replace('\\', '/');
}
