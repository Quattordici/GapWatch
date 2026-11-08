using System.Reflection;
using System.Runtime.Loader;
using GapWatch.Core.Findings;
using GapWatch.Sdk;

namespace GapWatch.Cli;

/// <summary>
/// Loads rule plugins from assemblies named in <c>gapwatch.json</c> or with <c>--plugin</c>. Nothing else is loaded:
/// no folder is scanned and no package is resolved.
/// </summary>
/// <remarks>
/// <para>
/// Each assembly gets its own <see cref="AssemblyLoadContext"/>, which resolves the plugin's dependencies from its
/// <c>.deps.json</c> or its folder, so they cannot clash with GapWatch's own or another plugin's. <c>GapWatch.Sdk</c> is
/// the exception: it always comes from GapWatch, so <see cref="IRulePlugin"/> and the model are one type on both sides.
/// </para>
/// <para>
/// Plugins run with the full trust of the GapWatch process, like Roslyn analyzers or MSBuild tasks. There is no
/// sandbox.
/// </para>
/// </remarks>
public static class PluginLoader
{
    private static readonly AssemblyName Sdk = typeof(IRulePlugin).Assembly.GetName();

    /// <summary>The plugins of every assembly in <paramref name="paths"/>, in order, with their rules checked.</summary>
    /// <exception cref="PluginException">An assembly cannot be loaded, or a prefix or rule id is invalid.</exception>
    public static RuleSet Load(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var plugins = new List<IRulePlugin>();
        var loaded = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in paths)
        {
            // The same assembly named twice (in the config and with --plugin) is loaded once.
            if (loaded.Add(Path.GetFullPath(path)))
            {
                plugins.AddRange(LoadAssembly(path));
            }
        }

        return RuleSet.With(plugins);
    }

    /// <summary>Every plugin class in one assembly.</summary>
    /// <exception cref="PluginException">The assembly cannot be loaded or holds no usable plugin.</exception>
    public static IReadOnlyList<IRulePlugin> LoadAssembly(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new PluginException($"Plugin not found: {path}");
        }

        Assembly assembly;
        try
        {
            assembly = new PluginLoadContext(fullPath).LoadFromAssemblyPath(fullPath);
        }
        catch (BadImageFormatException)
        {
            throw new PluginException($"Plugin {path} is not a .NET assembly.");
        }
        catch (FileLoadException ex)
        {
            throw new PluginException($"Plugin {path} could not be loaded: {ex.Message}");
        }

        CheckSdkVersion(assembly, path);

        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or FileNotFoundException or FileLoadException or TypeLoadException)
        {
            throw new PluginException($"Plugin {path} could not be loaded: {ex.Message}");
        }

        var pluginTypes = types
            .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && typeof(IRulePlugin).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
        if (pluginTypes.Count == 0)
        {
            throw new PluginException($"Plugin {path} has no public class that implements {nameof(IRulePlugin)}.");
        }

        return pluginTypes.Select(type => Create(type, path)).ToList();
    }

    private static IRulePlugin Create(Type type, string path)
    {
        if (type.GetConstructor(Type.EmptyTypes) is not { } constructor)
        {
            throw new PluginException($"Plugin class {type.FullName} in {path} needs a public parameterless constructor.");
        }

        try
        {
            return (IRulePlugin)constructor.Invoke(null);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            throw new PluginException($"Plugin class {type.FullName} in {path} failed to start: {inner.GetType().Name}: {inner.Message}", inner);
        }
    }

    /// <summary>
    /// A plugin works with this GapWatch when it was built against the same major version of <c>GapWatch.Sdk</c> and a
    /// minor version no newer than this one: minor releases only add to the SDK.
    /// </summary>
    internal static void CheckSdkVersion(Assembly assembly, string path)
    {
        var reference = assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == Sdk.Name);
        if (reference?.Version is not { } needed)
        {
            throw new PluginException($"Plugin {path} does not reference {Sdk.Name}, so it cannot contain GapWatch rules.");
        }

        var host = Sdk.Version!;
        if (needed.Major != host.Major || needed.Minor > host.Minor)
        {
            throw new PluginException(
                $"Plugin {path} was built against {Sdk.Name} {needed.ToString(3)}, which this GapWatch ({host.ToString(3)}) does not support. "
                + $"It runs plugins built against {host.Major}.0 to {host.Major}.{host.Minor}.");
        }
    }

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath))
    {
        private readonly AssemblyDependencyResolver resolver = new(pluginPath);
        private readonly string directory = Path.GetDirectoryName(pluginPath)!;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The SDK, and anything the host already has, such as the runtime, come from the default context.
            if (assemblyName.Name == Sdk.Name)
            {
                return null;
            }

            // The .deps.json of a published plugin lists its dependencies; without one, they are looked for next to it.
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null && assemblyName.Name is { } name && File.Exists(Path.Combine(directory, name + ".dll"))
                && !IsRuntime(name))
            {
                path = Path.Combine(directory, name + ".dll");
            }

            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;

        /// <summary>Runtime assemblies always come from the host, even when a copy sits next to the plugin.</summary>
        private static bool IsRuntime(string name) =>
            name.StartsWith("System.", StringComparison.Ordinal) || name is "System" or "mscorlib" or "netstandard";
    }
}
