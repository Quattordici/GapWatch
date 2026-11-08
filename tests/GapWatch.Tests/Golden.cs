using System.Text;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>
/// Golden-file assertions. Set <c>GAPWATCH_UPDATE_GOLDEN=1</c> to (re)write the expected files from the actual output,
/// then review the diff in version control like any other change.
/// </summary>
internal static class Golden
{
    public static void Verify(string fileName, string actual)
    {
        if (Environment.GetEnvironmentVariable("GAPWATCH_UPDATE_GOLDEN") == "1")
        {
            var target = Path.Combine(RepositoryRoot(), "tests", "GapWatch.Tests", "Golden", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Normalize(actual), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        var expectedPath = Path.Combine(AppContext.BaseDirectory, "Golden", fileName);
        File.Exists(expectedPath).ShouldBeTrue($"Missing golden file '{fileName}'. Run the tests with GAPWATCH_UPDATE_GOLDEN=1 to create it.");

        Normalize(actual).ShouldBe(Normalize(File.ReadAllText(expectedPath)));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GapWatch.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate GapWatch.slnx above the test output directory.");
    }
}
