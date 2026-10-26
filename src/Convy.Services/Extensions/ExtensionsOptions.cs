using Microsoft.Extensions.Configuration;

namespace Convy.Services.Extensions;

/// <summary>
/// The <c>extensions</c> section of configuration.yml: where the source extensions are (the packages Shizuka installs,
/// built against <c>Shizuka.Sources.Abstractions</c>) and what each of their sources gets.
/// </summary>
public sealed class ExtensionsOptions
{
    public const string SectionName = "Extensions";

    /// <summary>The folder with the extension packages (a <c>.zip</c> or a <c>.dll</c> each). Read at startup.</summary>
    [ConfigurationKeyName("path")]
    public string Path { get; set; } = "/data/extensions";

    /// <summary>
    /// Skip the package audit: an extension may then open its own connections and files. Only for extensions you built
    /// yourself, as Shizuka's <c>trust=true</c>.
    /// </summary>
    [ConfigurationKeyName("trusted")]
    public bool Trusted { get; set; }

    /// <summary>Per source, by its manifest id: settings, proxy, on/off. Hot-reloaded (the proxy needs a restart).</summary>
    [ConfigurationKeyName("settings")]
    public Dictionary<string, ExtensionEntryOptions> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings of one source, or an empty default.</summary>
    public ExtensionEntryOptions For(string sourceId) =>
        Settings.TryGetValue(sourceId, out var entry) ? entry : new ExtensionEntryOptions();
}

/// <summary>What one source of an extension gets from configuration.yml.</summary>
public sealed class ExtensionEntryOptions
{
    /// <summary>A disabled source is loaded but not listed.</summary>
    [ConfigurationKeyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Proxy for the source's requests, e.g. <c>socks5://tor:9050</c>; the environment's proxy otherwise.</summary>
    [ConfigurationKeyName("proxy")]
    public string? Proxy { get; set; }

    /// <summary>How long one request may take; the source's sites decide what is normal.</summary>
    [ConfigurationKeyName("timeout_sec")]
    public double TimeoutSeconds { get; set; } = 60;

    /// <summary>The source's own settings by key (<c>cookies</c>, …), as its manifest lists them; checked like Shizuka checks them.</summary>
    [ConfigurationKeyName("values")]
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
