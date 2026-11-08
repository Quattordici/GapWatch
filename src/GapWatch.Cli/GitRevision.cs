using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GapWatch.Core;

namespace GapWatch.Cli;

/// <summary>
/// A git revision as a <see cref="ISourceTree"/> (<c>--base-ref</c>), so a pull request build needs no separate step to
/// extract the base. It only reads: <c>git cat-file</c>, <c>git ls-tree</c> and <c>git rev-parse</c>. Nothing is checked
/// out or written.
/// </summary>
/// <remarks>
/// A path is resolved like any file path (relative to the current directory, or absolute), and git runs in the path
/// itself when it is a folder that exists, otherwise in its nearest folder that does. So the path may be one the head
/// has deleted, or the repository root. Labels are <c>rev:path</c>, with the path as given.
/// </remarks>
internal sealed class GitRevision(string revision) : ISourceTree
{
    /// <inheritdoc />
    /// <exception cref="ContractReadException">git is missing, the revision is unknown, or the path does not exist there.</exception>
    public EntryKind Kind(string path)
    {
        var (directory, spec) = Resolve(path);
        return Text(RunIn(directory, ["cat-file", "-t", spec], $"'{path}' does not exist at revision '{revision}'")).Trim() switch
        {
            "tree" => EntryKind.Folder,
            "blob" => EntryKind.File,
            _ => EntryKind.Missing,
        };
    }

    /// <inheritdoc />
    public SourceFile Read(string path)
    {
        var (directory, spec) = Resolve(path);
        var content = RunIn(directory, ["cat-file", "blob", spec], $"Could not read '{path}' at revision '{revision}'");
        return new SourceFile($"{revision}:{path.Replace('\\', '/')}", Path.GetFileName(path), content);
    }

    /// <inheritdoc />
    /// <remarks>Lists the tree once and reads every file with one <c>git cat-file --batch</c>, so a large folder costs two git processes.</remarks>
    public IReadOnlyList<SourceFile> ReadFolder(string folder, bool recursive, Func<string, bool> include)
    {
        ArgumentNullException.ThrowIfNull(include);

        var (directory, spec) = Resolve(folder);
        string[] list = recursive ? ["ls-tree", "-r", "-z", "--full-tree", spec] : ["ls-tree", "-z", "--full-tree", spec];

        // <mode> SP <type> SP <object> TAB <path> NUL
        var files = Text(RunIn(directory, list, $"Could not list '{folder}' at revision '{revision}'"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => (Header: entry[..entry.IndexOf('\t')].Split(' '), File: entry[(entry.IndexOf('\t') + 1)..]))
            .Where(e => e.Header is [_, "blob", _] && include(e.File))
            .OrderBy(e => e.File, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            return [];
        }

        var input = Encoding.ASCII.GetBytes(string.Concat(files.Select(f => f.Header[2] + "\n")));
        var batch = RunIn(directory, ["cat-file", "--batch"], $"Could not read '{folder}' at revision '{revision}'", input);
        var label = $"{revision}:{folder.Replace('\\', '/').TrimEnd('/')}";
        var result = new List<SourceFile>(files.Count);
        var offset = 0;
        foreach (var (_, file) in files)
        {
            // <object> SP blob SP <size> LF <content> LF
            var lineEnd = System.Array.IndexOf(batch, (byte)'\n', offset);
            var size = int.Parse(Encoding.ASCII.GetString(batch, offset, lineEnd - offset).Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
            result.Add(new SourceFile($"{label}/{file}", file[(file.LastIndexOf('/') + 1)..], batch[(lineEnd + 1)..(lineEnd + 1 + size)]));
            offset = lineEnd + 1 + size + 1;
        }

        return result;
    }

    /// <summary>
    /// The folder git runs in, and <c>rev:path-in-repository</c> for <paramref name="path"/>: the path itself when it is
    /// an existing folder (so the repository root works), otherwise its nearest existing ancestor.
    /// </summary>
    private (string Directory, string Spec) Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Directory.Exists(full) ? full : NearestExistingAncestor(full);
        var prefix = Text(RunIn(directory, ["rev-parse", "--show-prefix"], $"'{path}' is not in a git repository")).Trim();
        var rest = Path.GetRelativePath(directory, full).Replace('\\', '/');
        return (directory, $"{revision}:{(prefix + (rest == "." ? string.Empty : rest)).TrimEnd('/')}");
    }

    /// <summary>The nearest ancestor of <paramref name="fullPath"/> that exists on disk.</summary>
    private static string NearestExistingAncestor(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        while (directory is not null && !Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? Path.GetPathRoot(fullPath) ?? fullPath;
    }

    private static string Text(byte[] output) => Encoding.UTF8.GetString(output);

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
