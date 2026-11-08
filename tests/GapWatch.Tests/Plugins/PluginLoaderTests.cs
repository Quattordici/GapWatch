using GapWatch.Cli;
using GapWatch.Sdk;
using Shouldly;

namespace GapWatch.Tests.Plugins;

/// <summary>Loading plugins from disk through the CLI, as a user does: <c>--plugin</c> and <c>gapwatch.json</c>.</summary>
public sealed class PluginLoaderTests
{
    private const string Get = """ "get": { "responses": { "204": { "description": "d" } } } """;
    private const string Post = """ "post": { "responses": { "204": { "description": "d" } } } """;

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = GapWatchCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>A folder with <c>base.json</c> (GET /pets) and <c>head.json</c> (GET and a new, open POST /pets).</summary>
    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gapwatch-plugins-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Base = Write("base.json", TestSpec.Document($$"""{ "/pets": { {{Get}} } }"""));
            Head = Write("head.json", TestSpec.Document($$"""{ "/pets": { {{Get}}, {{Post}} } }"""));
        }

        public string Path { get; }

        public string Base { get; }

        public string Head { get; }

        public string Folder(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;

        public string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string Acme() => PluginAssembly.Compile(Folder("acme"), "Acme.GapWatch.Rules", PluginAssembly.Acme);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // A loaded plugin assembly stays locked on Windows until the process exits.
            }
            catch (IOException)
            {
            }
        }
    }

    [Test]
    public void A_plugin_given_with_the_option_reports_with_the_built_in_rules()
    {
        using var workspace = new Workspace();

        var (code, output, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", workspace.Acme());

        error.ShouldBeEmpty();
        code.ShouldBe(ExitCodes.FindingsAtThreshold);
        output.ShouldContain("ACME001");
        output.ShouldContain("New operations must require authentication.");
        output.ShouldContain("GW003");
    }

    [Test]
    public void Without_the_plugin_the_same_change_passes()
    {
        using var workspace = new Workspace();

        Run("compare", workspace.Base, workspace.Head).Code.ShouldBe(ExitCodes.Success);
    }

    [Test]
    public void A_plugin_named_in_the_config_is_found_relative_to_the_config_and_its_rules_can_be_configured()
    {
        using var workspace = new Workspace();
        workspace.Acme();
        var config = workspace.Write("config/gapwatch.json", """
            {
              "plugins": [ { "path": "../acme/Acme.GapWatch.Rules.dll" } ],
              "rules": { "ACME001": "warning" }
            }
            """);

        var (code, output, error) = Run("compare", workspace.Base, workspace.Head, "--config", config);

        error.ShouldBeEmpty();
        code.ShouldBe(ExitCodes.Success);
        output.ShouldContain("ACME001");
    }

    [Test]
    public void A_suppression_of_a_plugin_finding_works_like_any_other()
    {
        using var workspace = new Workspace();
        var config = workspace.Write("gapwatch.json", """
            { "suppressions": [ { "rule": "ACME001", "operation": "POST /pets", "justification": "Public by design." } ] }
            """);

        var (code, output, _) = Run("compare", workspace.Base, workspace.Head, "--config", config, "--plugin", workspace.Acme());

        code.ShouldBe(ExitCodes.Success);
        output.ShouldContain("Public by design.");
    }

    [Test]
    public void Configuration_for_a_plugin_rule_fails_when_the_plugin_is_not_loaded()
    {
        using var workspace = new Workspace();
        var config = workspace.Write("gapwatch.json", """{ "rules": { "ACME001": "off" } }""");

        var (code, _, error) = Run("compare", workspace.Base, workspace.Head, "--config", config);

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("Unknown rule id 'ACME001'");
    }

    [Test]
    public void The_same_plugin_in_the_config_and_on_the_command_line_is_loaded_once()
    {
        using var workspace = new Workspace();
        var plugin = workspace.Acme();
        var config = workspace.Write("gapwatch.json", """{ "plugins": [ { "path": "acme/Acme.GapWatch.Rules.dll" } ] }""");

        var (code, _, error) = Run("compare", workspace.Base, workspace.Head, "--config", config, "--plugin", plugin);

        error.ShouldBeEmpty();
        code.ShouldBe(ExitCodes.FindingsAtThreshold);
    }

    [Test]
    public void The_rule_list_shows_plugin_rules()
    {
        using var workspace = new Workspace();

        var (code, output, _) = Run("rules", "--plugin", workspace.Acme());

        code.ShouldBe(ExitCodes.Success);
        output.ShouldContain("ACME001  BREAKING  NewOperationWithoutSecurity (plugin Acme.GapWatch.Rules)");
    }

    [Test]
    public void A_missing_plugin_is_an_error()
    {
        using var workspace = new Workspace();

        var (code, _, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", "nowhere/Missing.dll");

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("Plugin not found: ");
        error.ShouldContain("Missing.dll");
    }

    [Test]
    public void A_file_that_is_not_an_assembly_is_an_error()
    {
        using var workspace = new Workspace();
        var notes = workspace.Write("notes.dll", "not an assembly");

        var (code, _, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", notes);

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("is not a .NET assembly");
    }

    [Test]
    public void An_assembly_without_a_plugin_class_is_an_error()
    {
        using var workspace = new Workspace();
        var empty = PluginAssembly.Compile(workspace.Folder("empty"), "Empty", "public static class Uses { public static System.Type T => typeof(GapWatch.Sdk.IRulePlugin); }");

        var (code, _, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", empty);

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("has no public class that implements IRulePlugin");
    }

    [Test]
    public void A_plugin_with_the_reserved_prefix_is_an_error()
    {
        using var workspace = new Workspace();
        var plugin = PluginAssembly.Compile(workspace.Folder("gw"), "Sneaky", PluginAssembly.Acme.Replace("ACME", "GWX", StringComparison.Ordinal));

        var (code, _, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", plugin);

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("Prefixes starting with 'GW' are reserved");
    }

    [Test]
    public void A_plugin_that_throws_fails_the_run_instead_of_being_skipped()
    {
        using var workspace = new Workspace();
        var plugin = PluginAssembly.Compile(workspace.Folder("boom"), "Boom", PluginAssembly.Acme.Replace(
            "if (pair is",
            "if (pair.Operation == \"POST /pets\") throw new System.InvalidOperationException(\"lookup table missing\");\n                if (pair is",
            StringComparison.Ordinal));

        var (code, output, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", plugin);

        code.ShouldBe(ExitCodes.Error);
        output.ShouldBeEmpty();
        error.ShouldContain("Plugin 'Boom' failed on operation 'POST /pets': InvalidOperationException: lookup table missing");
    }

    [Test]
    public void A_plugin_whose_constructor_throws_is_an_error()
    {
        using var workspace = new Workspace();
        var plugin = PluginAssembly.Compile(workspace.Folder("ctor"), "Ctor", PluginAssembly.Acme.Replace(
            "public string Prefix",
            "public AcmeRules() => throw new System.InvalidOperationException(\"no settings\");\n\n    public string Prefix",
            StringComparison.Ordinal));

        var (code, _, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", plugin);

        code.ShouldBe(ExitCodes.Error);
        error.ShouldContain("Plugin class AcmeRules in");
        error.ShouldContain("failed to start: InvalidOperationException: no settings");
    }

    [Test]
    [Arguments("2.0.0.0")]
    [Arguments("1.99.0.0")]
    [Arguments("0.9.0.0")]
    public void A_plugin_built_against_an_unsupported_sdk_version_is_an_error(string version)
    {
        using var workspace = new Workspace();
        var sdk = PluginAssembly.FakeSdk(workspace.Folder("fake-sdk"), version);
        var plugin = PluginAssembly.Compile(workspace.Folder("old"), "Versioned", "public sealed class Marker : GapWatch.Sdk.IRulePlugin { }", sdk);

        var (code, _, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", plugin);

        code.ShouldBe(ExitCodes.Error);
        var host = typeof(IRulePlugin).Assembly.GetName().Version!;
        error.ShouldContain($"was built against GapWatch.Sdk {new Version(version).ToString(3)}, which this GapWatch ({host.ToString(3)}) does not support");
    }

    [Test]
    public void A_dependency_next_to_the_plugin_is_loaded_into_the_plugin_context()
    {
        using var workspace = new Workspace();
        var folder = workspace.Folder("with-dependency");
        var helper = PluginAssembly.Compile(folder, "Acme.Text", "namespace Acme.Text { public static class Messages { public static string Open => \"Open endpoint (from Acme.Text).\"; } }");
        var plugin = PluginAssembly.Compile(folder, "Acme.Rules", PluginAssembly.Acme.Replace(
            "\"New operations must require authentication.\");\n",
            "Acme.Text.Messages.Open);\n",
            StringComparison.Ordinal), null, helper);

        var (code, output, error) = Run("compare", workspace.Base, workspace.Head, "--plugin", plugin);

        error.ShouldBeEmpty();
        code.ShouldBe(ExitCodes.FindingsAtThreshold);
        output.ShouldContain("Open endpoint (from Acme.Text).");
    }

    [Test]
    public void The_config_rejects_a_plugin_entry_without_a_path_or_with_unknown_properties()
    {
        Should.Throw<ConfigurationException>(() => ConfigFile.ParseConfiguration("""{ "plugins": [ {} ] }""", "c.json"))
            .Message.ShouldBe("c.json: $.plugins[0] needs a 'path' to the plugin assembly.");
        Should.Throw<ConfigurationException>(() => ConfigFile.ParseConfiguration("""{ "plugins": [ { "path": "a.dll", "package": "Acme" } ] }""", "c.json"))
            .Message.ShouldBe("c.json: unknown property '$.plugins[0].package'. Expected 'path'.");
        Should.Throw<ConfigurationException>(() => ConfigFile.ParseConfiguration("""{ "plugins": "a.dll" }""", "c.json"))
            .Message.ShouldBe("c.json: $.plugins must be an array, not string.");
    }
}
