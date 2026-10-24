using System.Reflection;
using System.Runtime.Loader;
using Convy.Sources;

namespace Convy.Services.Plugins;

/// <summary>
/// The load context of one plugin: collectible, so a reload unloads the old code once nothing refers to it.
/// <c>Convy.Sources.Abstractions</c> always comes from the host, whatever version the plugin asks for (compatibility
/// was checked before loading); the package's own assemblies are loaded from memory, here; anything else (the
/// framework) falls back to the default context.
/// </summary>
public sealed class PluginLoadContext(PluginPackage package)
    : AssemblyLoadContext($"Convy.Plugins:{package.Package}", isCollectible: true)
{
    private static readonly Assembly Api = typeof(SourcePlugin).Assembly;

    public Assembly LoadMain() => LoadFromAssemblyName(new AssemblyName(package.Package));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, SourcesApi.AssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return Api;
        }

        if (assemblyName.Name is not null && package.Assemblies.TryGetValue(assemblyName.Name, out var bytes))
        {
            return LoadFromStream(new MemoryStream(bytes, writable: false));
        }

        return null;
    }
}
