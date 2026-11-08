using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using GapWatch.Sdk;

namespace GapWatch.Tests.Plugins;

/// <summary>
/// Compiles plugin assemblies for the loader tests, so they load a real file from disk the way the CLI does. Each test
/// compiles into its own folder.
/// </summary>
internal static class PluginAssembly
{
    private static readonly Lazy<List<MetadataReference>> Runtime = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileName(path) is var name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList());

    /// <summary>The ACME001 plugin from the design, as source.</summary>
    public const string Acme = """
        using System.Collections.Generic;
        using System.Linq;
        using GapWatch.Core.Findings;
        using GapWatch.Sdk;

        public sealed class AcmeRules : IRulePlugin
        {
            public string Prefix => "ACME";

            public IReadOnlyList<RuleDescriptor> Rules { get; } =
                [new RuleDescriptor("ACME001", "NewOperationWithoutSecurity", Severity.Breaking, "New operations must require authentication.")];

            public IEnumerable<PluginFinding> Check(OperationPair pair, RuleContext context)
            {
                if (pair is { Baseline: null, Head: { } head } && (head.Security.Count == 0 || head.Security.Contains("none")))
                {
                    yield return new PluginFinding("ACME001", FindingLocation.Operation.Instance, "New operations must require authentication.");
                }
            }
        }
        """;

    /// <summary>
    /// Compiles <paramref name="source"/> to <c>{name}.dll</c> in <paramref name="folder"/> against the real SDK (or
    /// <paramref name="sdk"/>) and any <paramref name="references"/>, and returns its path.
    /// </summary>
    public static string Compile(string folder, string name, string source, string? sdk = null, params string[] references)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            Runtime.Value
                .Append(MetadataReference.CreateFromFile(sdk ?? typeof(IRulePlugin).Assembly.Location))
                .Concat(references.Select(r => MetadataReference.CreateFromFile(r))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var path = Path.Combine(folder, name + ".dll");
        var result = compilation.Emit(path);
        if (!result.Success)
        {
            throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        return path;
    }

    /// <summary>A stand-in <c>GapWatch.Sdk</c> with another version, to build a plugin the host must refuse.</summary>
    public static string FakeSdk(string folder, string version)
    {
        Directory.CreateDirectory(folder);
        var compilation = CSharpCompilation.Create(
            "GapWatch.Sdk",
            [CSharpSyntaxTree.ParseText($$"""
                [assembly: System.Reflection.AssemblyVersion("{{version}}")]
                namespace GapWatch.Sdk { public interface IRulePlugin { } }
                """)],
            Runtime.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(folder, "GapWatch.Sdk.dll");
        compilation.Emit(path).Success.ShouldBeTrueOrThrow();
        return path;
    }

    private static void ShouldBeTrueOrThrow(this bool success)
    {
        if (!success)
        {
            throw new InvalidOperationException("The fake SDK did not compile.");
        }
    }
}
