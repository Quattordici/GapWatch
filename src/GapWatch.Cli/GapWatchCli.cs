using System.Reflection;
using System.Text;
using GapWatch.Core;
using GapWatch.Core.Comparison;
using GapWatch.Core.Findings;
using GapWatch.OpenApi;
using GapWatch.Reporting;

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
          gapwatch rules [--format console|markdown]
          gapwatch --version | --help

        compare options:
          --format <console|markdown>     Output format (default: console).
          --output <file>                 Write the report to a file instead of stdout.
          --fail-on <breaking|warning|none>
                                          Lowest severity that makes the exit code 1 (default: breaking).

        Inputs are OpenAPI 3.x documents in JSON. <base> is what consumers rely on today,
        <head> is the proposed change.

        Exit codes:
          0  no finding at or above the --fail-on threshold
          1  at least one finding at or above the threshold
          2  usage error or unreadable input
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
        catch (UsageException ex)
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
        var options = Options.Parse(args, allowed: ["--format", "--output", "--fail-on"]);
        if (options.Positional.Count != 2)
        {
            throw new UsageException("compare needs exactly two inputs: <base> <head>.");
        }

        var format = options.Format();
        var failOn = options.FailOn();

        var baselinePath = options.Positional[0];
        var headPath = options.Positional[1];

        var baseline = ContractLoader.Load(baselinePath);
        var head = ContractLoader.Load(headPath);
        var result = new ContractComparer().Compare(baseline, head);

        IReportRenderer renderer = format == "markdown" ? new MarkdownReportRenderer() : new ConsoleReportRenderer();
        var report = renderer.Render(result, new ReportContext(baselinePath, headPath));

        if (options.Values.TryGetValue("--output", out var outputPath))
        {
            File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output.WriteLine(
                $"Report written to {outputPath} ({result.Count(Severity.Breaking)} breaking, {result.Count(Severity.Warning)} warning(s), {result.Count(Severity.Info)} info).");
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
        var options = Options.Parse(args, allowed: ["--format"]);
        if (options.Positional.Count != 0)
        {
            throw new UsageException("rules takes no positional arguments.");
        }

        output.Write(options.Format() == "markdown" ? RuleListRenderer.RenderMarkdown() : RuleListRenderer.RenderConsole());
        return ExitCodes.Success;
    }

    private static int PrintUsage(TextWriter output)
    {
        output.WriteLine(Usage);
        return ExitCodes.Success;
    }

    private static int PrintVersion(TextWriter output)
    {
        var version = typeof(GapWatchCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        output.WriteLine(version.Split('+')[0]);
        return ExitCodes.Success;
    }

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
            }

            return options;
        }

        public string Format()
        {
            var format = Values.GetValueOrDefault("--format", "console").ToLowerInvariant();
            return format is "console" or "markdown"
                ? format
                : throw new UsageException($"Unknown format '{format}'. Use 'console' or 'markdown'.");
        }

        /// <summary>Returns the threshold, or <c>null</c> when failing is disabled.</summary>
        public Severity? FailOn() =>
            Values.GetValueOrDefault("--fail-on", "breaking").ToLowerInvariant() switch
            {
                "breaking" => Severity.Breaking,
                "warning" => Severity.Warning,
                "none" => null,
                var other => throw new UsageException($"Unknown --fail-on value '{other}'. Use 'breaking', 'warning' or 'none'."),
            };
    }
}
