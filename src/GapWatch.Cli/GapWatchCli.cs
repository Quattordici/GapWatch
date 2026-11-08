using System.Reflection;
using System.Text;
using GapWatch.Core;
using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.Core.Policy;
using GapWatch.OpenApi;
using GapWatch.Reporting;
using GapWatch.Roslyn;

namespace GapWatch.Cli;

/// <summary>Process exit codes. Stable: CI pipelines are expected to depend on them.</summary>
public static class ExitCodes
{
    /// <summary>The command succeeded and no finding reached the failure threshold.</summary>
    public const int Success = 0;

    /// <summary>At least one finding reached the <c>--fail-on</c> threshold.</summary>
    public const int FindingsAtThreshold = 1;

    /// <summary>Bad arguments, unreadable input or any other error.</summary>
    public const int Error = 2;
}

/// <summary>Command-line entry point, kept free of <see cref="Console"/> so it can be tested.</summary>
public static class GapWatchCli
{
    private const string Usage = """
        GapWatch - detect breaking changes between two versions of an HTTP API contract.

        Usage:
          gapwatch compare <base> <head> [options]
          gapwatch rules [--format console|markdown] [--config <file>] [--plugin <file>]...
          gapwatch --version | --help

        compare options:
          --format <console|markdown|sarif|junit>
                                          Output format (default: console).
          --output <file>                 Write the report to a file instead of stdout.
          --fail-on <breaking|warning|none>
                                          Lowest severity that makes the exit code 1 (default: breaking, or
                                          failOn in the config file).
          --config <file>                 gapwatch.json with severity overrides and suppressions.
          --source <dir>                  C# source of the head API. Maps findings to the controller action,
                                          minimal API endpoint, parameter or DTO property behind them.
          --baseline <file>               Accept the findings listed in a baseline file (exact matches only).
          --write-baseline <file>         Write every current finding to a baseline file, then report with it.
          --base-ref <rev>                Read <base> from a git revision (e.g. origin/main) instead of the working
                                          tree. <base> is then a path in the repository, usually the same as <head>.
          --input <openapi|csharp>        What <base> and <head> are (default: openapi). With csharp, they are folders
                                          of ASP.NET Core source and the contract is read from the code.
          --plugin <file>                 Load custom rules from a plugin assembly, in addition to the plugins in
                                          the config file. Repeatable. Plugins run with full trust: load only
                                          assemblies you would also run in your build.

        Inputs are OpenAPI 3.x documents in JSON or YAML. <base> is what consumers rely on today,
        <head> is the proposed change. With one document per API version, pass two folders: documents
        pair by info.version (or by file name), and a document on one side only is a removed or added version.
        With --input csharp, pass two source folders (or one folder twice with --base-ref).

        Exit codes:
          0  no finding at or above the --fail-on threshold
          1  at least one finding at or above the threshold
          2  usage error, unreadable input, or a plugin that cannot be loaded or fails
        """;

    /// <summary>Runs the CLI and returns the process exit code.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            error.WriteLine(Usage);
            return ExitCodes.Error;
        }

        try
        {
            return args[0] switch
            {
                "-h" or "--help" or "help" => PrintUsage(output),
                "--version" or "version" => PrintVersion(output),
                "compare" => Compare(args[1..], output, error),
                "rules" => Rules(args[1..], output, error),
                _ => Fail(error, $"Unknown command '{args[0]}'. Run 'gapwatch --help'."),
            };
        }
        catch (ContractReadException ex)
        {
            return Fail(error, ex.Message);
        }
        catch (ConfigurationException ex)
        {
            return Fail(error, ex.Message);
        }
        catch (UsageException ex)
        {
            return Fail(error, ex.Message);
        }
        catch (InputException ex)
        {
            return Fail(error, ex.Message);
        }
        catch (PluginException ex)
        {
            return Fail(error, ex.Message);
        }
        catch (IOException ex)
        {
            return Fail(error, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(error, ex.Message);
        }
    }

    private static int Compare(string[] args, TextWriter output, TextWriter error)
    {
        var options = Options.Parse(args, allowed: ["--format", "--output", "--fail-on", "--config", "--source", "--baseline", "--write-baseline", "--base-ref", "--input", "--plugin"]);
        if (options.Positional.Count != 2)
        {
            throw new UsageException("compare needs exactly two inputs: <base> <head>.");
        }

        var format = options.Format();
        var input = options.Input();

        var (configuration, rules) = LoadConfiguration(options);
        var failOn = options.FailOn(configuration.FailOn);

        var basePath = options.Positional[0];
        var headPath = options.Positional[1];

        options.Values.TryGetValue("--base-ref", out var baseRef);
        ISourceTree baseTree = baseRef is null ? WorkingTree.Instance : new GitRevision(baseRef);
        var (baseContract, head, fallbackHeadDocument) = ContractInputs.Load(
            baseTree, basePath, WorkingTree.Instance, headPath, input == "csharp" ? InputKind.CSharp : InputKind.OpenApi);
        var baseLabel = baseRef is null ? basePath : $"{baseRef}:{basePath}";
        var accepted = options.Values.TryGetValue("--baseline", out var baselinePath)
            ? BaselineFile.Load(baselinePath)
            : Baseline.Empty;
        var check = CompatibilityCheck.Run(baseContract, head, configuration, accepted, DateOnly.FromDateTime(DateTime.UtcNow), rules);
        var result = check.Result;
        if (options.Values.TryGetValue("--write-baseline", out var newBaselinePath))
        {
            // Keep an edited justification: from the baseline in use, or from the file being replaced.
            var justification = baselinePath is not null ? accepted.Justification
                : File.Exists(newBaselinePath) ? BaselineFile.Load(newBaselinePath).Justification
                : BaselineFile.DefaultJustification;
            var written = Baseline.From(check.BeforeBaseline.Findings, justification);
            BaselineFile.Save(newBaselinePath, written);
            error.WriteLine($"Baseline with {written.Entries.Count} finding(s) written to {newBaselinePath}.");
            result = written.Apply(check.BeforeBaseline);
        }

        if (options.Values.TryGetValue("--source", out var sourceDirectory))
        {
            result = CodeMap.Load(sourceDirectory).Annotate(result);
        }

        IReportRenderer renderer = format switch
        {
            "markdown" => new MarkdownReportRenderer(),
            "sarif" => new SarifReportRenderer(),
            "junit" => new JUnitReportRenderer(),
            _ => new ConsoleReportRenderer(),
        };
        var context = new ReportContext(baseLabel, headPath)
        {
            ToolVersion = ToolVersion(),
            FailOn = failOn,
            FallbackHeadDocument = fallbackHeadDocument,
            Rules = rules,
        };
        var report = renderer.Render(result, context);

        if (options.Values.TryGetValue("--output", out var outputPath))
        {
            File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output.WriteLine(
                $"Report written to {outputPath} ({result.Count(Severity.Breaking)} breaking, {result.Count(Severity.Warning)} warning(s), {result.Count(Severity.Info)} info, {result.Suppressed.Count} suppressed).");
        }
        else
        {
            output.Write(report);
        }

        return failOn is { } threshold && result.HasAtLeast(threshold)
            ? ExitCodes.FindingsAtThreshold
            : ExitCodes.Success;
    }

    private static int Rules(string[] args, TextWriter output, TextWriter error)
    {
        var options = Options.Parse(args, allowed: ["--format", "--config", "--plugin"]);
        if (options.Positional.Count != 0)
        {
            throw new UsageException("rules takes no positional arguments.");
        }

        var format = options.Format();
        if (format is not ("console" or "markdown"))
        {
            throw new UsageException($"rules supports 'console' and 'markdown', not '{format}'.");
        }

        var (_, rules) = LoadConfiguration(options);
        output.Write(format == "markdown" ? RuleListRenderer.RenderMarkdown(rules) : RuleListRenderer.RenderConsole(rules));
        return ExitCodes.Success;
    }

    /// <summary>
    /// The configuration file, if any, and the rules of the run: the built-in ones plus the plugins named in the file
    /// (relative to it) and with <c>--plugin</c> (relative to the working directory). With plugins, the policy is
    /// validated once their rule ids are known.
    /// </summary>
    private static (GapWatchConfiguration Configuration, RuleSet Rules) LoadConfiguration(Options options)
    {
        var configuration = options.Values.TryGetValue("--config", out var configPath)
            ? ConfigFile.Read(configPath)
            : GapWatchConfiguration.Default;

        var plugins = configuration.Plugins.Concat(options.Repeated("--plugin").Select(Path.GetFullPath)).ToList();
        var rules = plugins.Count == 0 ? RuleSet.BuiltIn : PluginLoader.Load(plugins);
        if (configPath is not null)
        {
            ConfigFile.Validate(configuration, rules, configPath);
        }

        return (configuration, rules);
    }

    private static int PrintUsage(TextWriter output)
    {
        output.WriteLine(Usage);
        return ExitCodes.Success;
    }

    private static int PrintVersion(TextWriter output)
    {
        output.WriteLine(ToolVersion());
        return ExitCodes.Success;
    }

    private static string ToolVersion() =>
        (typeof(GapWatchCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine($"error: {message}");
        return ExitCodes.Error;
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Options
    {

        public List<string> Positional { get; } = [];

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>Options that may be given more than once, with every value in order.</summary>
        private Dictionary<string, List<string>> All { get; } = new(StringComparer.Ordinal);

        /// <summary>Every value of a repeatable option, in order; empty when it was not given.</summary>
        public IReadOnlyList<string> Repeated(string name) => All.GetValueOrDefault(name) ?? [];

        public static Options Parse(string[] args, string[] allowed)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var argument = args[i];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    options.Positional.Add(argument);
                    continue;
                }

                string name;
                string value;
                var equals = argument.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    name = argument[..equals];
                    value = argument[(equals + 1)..];
                }
                else
                {
                    name = argument;
                    if (i + 1 >= args.Length)
                    {
                        throw new UsageException($"Option '{name}' needs a value.");
                    }

                    value = args[++i];
                }

                if (!allowed.Contains(name, StringComparer.Ordinal))
                {
                    throw new UsageException($"Unknown option '{name}'.");
                }

                options.Values[name] = value;
                if (!options.All.TryGetValue(name, out var values))
                {
                    options.All[name] = values = [];
                }

                values.Add(value);
            }

            return options;
        }

        public string Format()
        {
            var format = Values.GetValueOrDefault("--format", "console").ToLowerInvariant();
            return format is "console" or "markdown" or "sarif" or "junit"
                ? format
                : throw new UsageException($"Unknown format '{format}'. Use 'console', 'markdown', 'sarif' or 'junit'.");
        }

        public string Input()
        {
            var input = Values.GetValueOrDefault("--input", "openapi").ToLowerInvariant();
            return input is "openapi" or "csharp"
                ? input
                : throw new UsageException($"Unknown input '{input}'. Use 'openapi' or 'csharp'.");
        }

        /// <summary>
        /// Returns the threshold, or <c>null</c> when failing is disabled. The option wins over the configured value, which
        /// wins over the default, <c>breaking</c>.
        /// </summary>
        public Severity? FailOn(string? configured) =>
            Values.GetValueOrDefault("--fail-on", configured ?? "breaking").ToLowerInvariant() switch
            {
                "breaking" => Severity.Breaking,
                "warning" => Severity.Warning,
                "none" => null,
                var other => throw new UsageException($"Unknown --fail-on value '{other}'. Use 'breaking', 'warning' or 'none'."),
            };
    }
}
