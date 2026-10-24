using Microsoft.Extensions.Configuration;

namespace Convy.Services.Plugins;

/// <summary>The <c>plugins</c> section of configuration.yml: where the plugin packages are and what each plugin gets.</summary>
public sealed class PluginsOptions
{
    public const string SectionName = "Plugins";

    /// <summary>The folder with the plugin packages (a <c>.dll</c> or a <c>.zip</c> each). Read at startup.</summary>
    [ConfigurationKeyName("path")]
    public string Path { get; set; } = "/data/plugins";

    /// <summary>
    /// Skip the package audit (<see cref="PluginAudit"/>): a plugin may then open its own connections and files.
    /// Only for plugins you built yourself.
    /// </summary>
    [ConfigurationKeyName("trusted")]
    public bool Trusted { get; set; }

    /// <summary>Per plugin, by its manifest id: settings, proxy, on/off. Hot-reloaded (the proxy needs a restart).</summary>
    [ConfigurationKeyName("settings")]
    public Dictionary<string, PluginEntryOptions> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings of one plugin, or an empty default.</summary>
    public PluginEntryOptions For(string pluginId) =>
        Settings.TryGetValue(pluginId, out var entry) ? entry : new PluginEntryOptions();
}

/// <summary>What one plugin gets from configuration.yml.</summary>
public sealed class PluginEntryOptions
{
    /// <summary>A disabled plugin is loaded but lists no sources.</summary>
    [ConfigurationKeyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Proxy for the plugin's requests, e.g. <c>socks5://tor:9050</c>; the environment's proxy otherwise.</summary>
    [ConfigurationKeyName("proxy")]
    public string? Proxy { get; set; }

    /// <summary>How long one request may take; the plugin's sites decide what is normal.</summary>
    [ConfigurationKeyName("timeout_sec")]
    public double TimeoutSeconds { get; set; } = 60;

    /// <summary>The plugin's own settings by key (<c>cookies</c>, …), as its manifest lists them.</summary>
    [ConfigurationKeyName("values")]
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
