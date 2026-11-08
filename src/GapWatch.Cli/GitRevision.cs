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
/// extract it. It only reads: <c>git cat-file</c>, <c>git ls-tree</c> and <c>git show</c>. Nothing is checked out or
/// written.
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
        var names = Text(Run(path, ["ls-tree", "--name-only", Spec(revision, folder) + "/"], $"Could not list '{path}' at revision '{revision}'"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name[(name.LastIndexOf('/') + 1)..])
            .Where(ContractLoader.IsSupported)
            .Order(StringComparer.Ordinal)
            .ToList();

        return names.Count > 0
            ? names.Select(name => new NamedContract($"{revision}:{folder}/{name}", Load(revision, $"{folder}/{name}"))).ToList()
            : throw new ContractReadException($"No OpenAPI documents (.json, .yaml, .yml) in folder '{path}' at revision '{revision}'.");
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

    private static byte[] Run(string path, IReadOnlyList<string> arguments, string failure)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = WorkingDirectory(Path.GetFullPath(path)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();

            return process.ExitCode == 0
                ? output.ToArray()
                : throw new ContractReadException($"{failure}: {error.Result.Trim()}");
        }
    }
}
