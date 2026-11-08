using System.Xml;
using System.Xml.Linq;

namespace GapWatch.Roslyn;

/// <summary>
/// The one MSBuild property the C# reader needs from the project: <c>&lt;Nullable&gt;</c>. Read from the project file
/// and <c>Directory.Build.props</c> as text, without MSBuild, so it works the same for the working tree and for a git
/// revision.
/// </summary>
/// <remarks>
/// Whether reference types are nullable changes the contract: in a nullable-oblivious project, <c>string Name</c> may
/// be null in a response and is optional in a minimal API request. Reading every project as if nullable were enabled
/// reported the day a project turned it on as many responses becoming nullable, and missed the requests that
/// stopped accepting null.
/// </remarks>
public static class ProjectSettings
{
    /// <summary>The project file name pattern and the props file MSBuild imports automatically.</summary>
    private const string ProjectExtension = ".csproj";

    private const string BuildProps = "Directory.Build.props";

    /// <summary>Whether a file, by name, is one <see cref="FindNullable"/> needs to see.</summary>
    public static bool IsProjectFile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, BuildProps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether nullable annotations are enabled for the source in <paramref name="folder"/>. Looks in the folder and then
    /// each parent, up to the one holding <c>.git</c>, for the nearest <c>.csproj</c> and the nearest
    /// <c>Directory.Build.props</c> (the only one MSBuild imports by default), and applies <see cref="NullableEnabled"/>.
    /// </summary>
    /// <param name="folder">The source folder.</param>
    /// <param name="projectFilesIn">
    /// The project files (<see cref="IsProjectFile"/>) directly in a folder, as name and text; empty when there are none
    /// or the folder does not exist in the tree being read.
    /// </param>
    public static bool FindNullable(string folder, Func<string, IReadOnlyList<(string Name, string Text)>> projectFilesIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(projectFilesIn);

        string? project = null;
        string? props = null;
        for (var directory = Path.GetFullPath(folder); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var files = projectFilesIn(directory);
            project ??= files
                .Where(f => f.Name.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.Text)
                .FirstOrDefault();
            props ??= files.FirstOrDefault(f => string.Equals(f.Name, BuildProps, StringComparison.OrdinalIgnoreCase)).Text;

            if ((project is not null && props is not null) || Path.Exists(Path.Combine(directory, ".git")))
            {
                break;
            }
        }

        return NullableEnabled(project, props);
    }

    /// <summary>
    /// Whether nullable annotations are enabled, given the text of the project file and of <c>Directory.Build.props</c>
    /// (either null when missing). The project wins over the props file, as MSBuild evaluates it later. <c>enable</c>
    /// and <c>annotations</c> enable annotations; <c>disable</c> and <c>warnings</c> do not. With a project file but no
    /// <c>&lt;Nullable&gt;</c>, annotations are disabled, the SDK default. With no project file at all, nothing is known,
    /// so they are taken as enabled, as in every current project template. Conditions on properties are not evaluated.
    /// </summary>
    public static bool NullableEnabled(string? projectFile, string? buildProps)
    {
        var value = NullableValue(projectFile) ?? NullableValue(buildProps);
        if (value is null)
        {
            return projectFile is null;
        }

        return value.Equals("enable", StringComparison.OrdinalIgnoreCase)
               || value.Equals("annotations", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The last <c>&lt;Nullable&gt;</c> value in a project file, or null when there is none or it is not XML.</summary>
    private static string? NullableValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(text).Descendants()
                .Where(e => e.Name.LocalName == "Nullable" && e.Parent?.Name.LocalName == "PropertyGroup")
                .Select(e => e.Value.Trim())
                .LastOrDefault(v => v.Length > 0);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
