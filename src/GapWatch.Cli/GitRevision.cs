using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GapWatch.Core;
using GapWatch.Core.Comparison;
using GapWatch.Core.Model;
using GapWatch.OpenApi;

namespace GapWatch.Cli;

/// <summary>
/// Reads the base contract from a git revision (<c>--base-ref</c>), so a pull request build needs no separate step to
/// extract it. It only reads: <c>git cat-file</c>, <c>git ls-tree</c>, <c>git rev-parse</c> and <c>git show</c>. Nothing
/// is checked out or written.
/// </summary>
/// <remarks>
/// The path is resolved like any file path (relative to the current directory, or absolute), and git runs in its nearest
/// folder that exists in the working tree, so the path may be one the head has deleted. Reports show the base as
/// <c>rev:path</c>, with the path as given.
/// </remarks>
internal static class GitRevision
{
    /// <summary>Whether <paramref name="path"/> at <paramref name="revision"/> is a folder (a git tree).</summary>
    /// <exception cref="ContractReadException">git is missing, the revision is unknown, or the path does not exist there.</exception>
    public static bool IsFolder(string revision, string path) =>
        Text(Run(path, ["cat-file", "-t", Spec(revision, path)], $"'{path}' does not exist at revision '{revision}'")).Trim() == "tree";

    /// <summary>Reads one document at the revision.</summary>
    public static ApiContract Load(string revision, string path)
    {
        using var content = new MemoryStream(Run(path, ["show", Spec(revision, path)], $"Could not read '{path}' at revision '{revision}'"));
        return ContractLoader.Load(path, content);
    }

    /// <summary>Reads every supported document directly in the folder at the revision, ordered by file name.</summary>
    public static IReadOnlyList<NamedContract> LoadFolder(string revision, string path)
    {
        var folder = path.Replace('\\', '/').TrimEnd('/');
        var names = Text(Run(path, ["ls-tree", "--name-only", "--full-tree", Spec(revision, folder) + "/"], $"Could not list '{path}' at revision '{revision}'"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name[(name.LastIndexOf('/') + 1)..])
            .Where(ContractLoader.IsSupported)
            .Order(StringComparer.Ordinal)
            .ToList();

        return names.Count > 0
            ? names.Select(name => new NamedContract($"{revision}:{folder}/{name}", Load(revision, $"{folder}/{name}"))).ToList()
            : throw new ContractReadException($"No OpenAPI documents (.json, .yaml, .yml) in folder '{path}' at revision '{revision}'.");
    }

    /// <summary>
    /// Every <c>.cs</c> file under the folder at the revision (skipping <c>bin</c>, <c>obj</c> and <c>.git</c>), for
    /// <c>--input csharp</c>. Paths are reported as <c>rev:folder/file.cs</c>. The files are read with one
    /// <c>git cat-file --batch</c>, so a large project costs three git processes, not one per file.
    /// </summary>
    public static IReadOnlyList<(string Path, string Text)> ReadSources(string revision, string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Directory.Exists(full) ? full : WorkingDirectory(full);
        var prefix = Text(RunIn(directory, ["rev-parse", "--show-prefix"], $"'{path}' is not in a git repository")).Trim();
        var rest = Path.GetRelativePath(directory, full).Replace('\\', '/');
        var tree = $"{revision}:{(prefix + (rest == "." ? string.Empty : rest)).TrimEnd('/')}";

        var kind = Text(RunIn(directory, ["cat-file", "-t", tree], $"'{path}' does not exist at revision '{revision}'")).Trim();
        if (kind != "tree")
        {
            throw new ContractReadException($"'{path}' is not a folder at revision '{revision}'; --input csharp needs a folder of C# source.");
        }

        // <mode> SP <type> SP <object> TAB <path> NUL
        var files = Text(RunIn(directory, ["ls-tree", "-r", "-z", "--full-tree", tree], $"Could not list '{path}' at revision '{revision}'"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => (Header: entry[..entry.IndexOf('\t')].Split(' '), File: entry[(entry.IndexOf('\t') + 1)..]))
            .Where(e => e.Header is [_, "blob", _] && e.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && !e.File.Split('/').SkipLast(1).Any(segment => segment is "bin" or "obj" or ".git" or "node_modules"))
            .OrderBy(e => e.File, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            return [];
        }

        var input = Encoding.ASCII.GetBytes(string.Concat(files.Select(f => f.Header[2] + "\n")));
        var batch = RunIn(directory, ["cat-file", "--batch"], $"Could not read '{path}' at revision '{revision}'", input);
        var folder = path.Replace('\\', '/').TrimEnd('/');
        var result = new List<(string, string)>(files.Count);
        var offset = 0;
        foreach (var (_, file) in files)
        {
            // <object> SP blob SP <size> LF <content> LF
            var lineEnd = System.Array.IndexOf(batch, (byte)'\n', offset);
            var size = int.Parse(Encoding.ASCII.GetString(batch, offset, lineEnd - offset).Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
            var text = Encoding.UTF8.GetString(batch, lineEnd + 1, size);
            result.Add(($"{revision}:{folder}/{file}", text.TrimStart('\uFEFF')));
            offset = lineEnd + 1 + size + 1;
        }

        return result;
    }

    /// <summary><c>rev:./relative</c>, relative to the folder <see cref="WorkingDirectory"/> picks for the path.</summary>
    private static string Spec(string revision, string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(WorkingDirectory(full), full).Replace('\\', '/');
        return $"{revision}:./{relative}";
    }

    /// <summary>The nearest ancestor of <paramref name="fullPath"/> that exists on disk.</summary>
    private static string WorkingDirectory(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        while (directory is not null && !Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? Path.GetPathRoot(fullPath) ?? fullPath;
    }

    private static string Text(byte[] output) => Encoding.UTF8.GetString(output);

    private static byte[] Run(string path, IReadOnlyList<string> arguments, string failure) =>
        RunIn(WorkingDirectory(Path.GetFullPath(path)), arguments, failure);

    private static byte[] RunIn(string workingDirectory, IReadOnlyList<string> arguments, string failure, byte[]? input = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new ContractReadException("Could not start git.");
        }
        catch (Win32Exception ex)
        {
            throw new ContractReadException($"--base-ref needs git on the PATH: {ex.Message}", ex);
        }

        using (process)
        {
            using var output = new MemoryStream();
            var error = process.StandardError.ReadToEndAsync();
            var writing = input is null
                ? Task.CompletedTask
                : Task.Run(() =>
                {
                    using var stdin = process.StandardInput.BaseStream;
                    stdin.Write(input);
                });
            process.StandardOutput.BaseStream.CopyTo(output);
            writing.GetAwaiter().GetResult();
            process.WaitForExit();

            return process.ExitCode == 0
                ? output.ToArray()
                : throw new ContractReadException($"{failure}: {error.Result.Trim()}");
        }
    }
}
