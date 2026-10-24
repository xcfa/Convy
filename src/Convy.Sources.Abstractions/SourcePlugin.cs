namespace Convy.Sources;

/// <summary>
/// The entry point of a plugin: one class per package, with a parameterless constructor, that the host finds in the
/// plugin's main assembly (the one referencing <c>Convy.Sources.Abstractions</c>). The host loads the package into its
/// own collectible load context, gives the plugin a <see cref="IPluginContext"/> (HTTP, settings, log) and lists its
/// sources like those of the built-in providers.
/// </summary>
public abstract class SourcePlugin
{
    /// <summary>What the plugin is: id, name, settings, hosts it talks to.</summary>
    public abstract PluginManifest Manifest { get; }

    /// <summary>
    /// The plugin's sources over <paramref name="context"/>, in display order. Called once per load (and again when
    /// the host reloads the plugin); the sources read live settings from the context on every call.
    /// </summary>
    public abstract IReadOnlyList<IContentSource> CreateSources(IPluginContext context);
}

/// <summary>What a plugin declares about itself.</summary>
public sealed record PluginManifest
{
    /// <summary>Stable id in lower case (<c>e-hentai</c>): the key of its settings in the host's configuration.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The hosts the plugin's requests may go to (<c>e-hentai.org</c>, <c>api.mangadex.org</c>); a subdomain of a listed
    /// host is allowed too. The host's HTTP refuses any other address. Empty — nothing is allowed.
    /// </summary>
    public IReadOnlyList<string> Domains { get; init; } = [];

    /// <summary>The settings the plugin reads from <see cref="IPluginContext.Settings"/>, for documentation and the UI.</summary>
    public IReadOnlyList<PluginSetting> Settings { get; init; } = [];

    /// <summary>At most this many requests per second go to the plugin's hosts; <c>null</c> — no limit.</summary>
    public double? RequestsPerSecond { get; init; }
}

/// <summary>A setting a plugin reads.</summary>
/// <param name="Key">The key under the plugin in the host's configuration.</param>
/// <param name="Name">What to show for it.</param>
/// <param name="Description">What it does and how to get it.</param>
/// <param name="Secret">Whether it is a credential (cookies, a key): never shown back.</param>
/// <param name="Required">Whether the plugin's sources do not work without it.</param>
public sealed record PluginSetting(string Key, string Name, string? Description = null, bool Secret = false, bool Required = false);

/// <summary>What the host gives a plugin: HTTP, its settings and a log. A plugin never opens its own connections.</summary>
public interface IPluginContext
{
    /// <summary>HTTP to the plugin's <see cref="PluginManifest.Domains"/>, with the proxy and rate limit the host applies.</summary>
    IPluginHttp Http { get; }

    /// <summary>
    /// The plugin's settings as the host has them now (hot-reloaded: read on every call, never cached). Keys are those of
    /// <see cref="PluginManifest.Settings"/>; a missing one reads as <c>null</c>.
    /// </summary>
    IPluginSettings Settings { get; }

    /// <summary>An HTML parser with CSS selectors, so a plugin needs no parser of its own (the audit refuses most).</summary>
    IPluginHtml Html { get; }

    IPluginLog Log { get; }
}

/// <summary>Parses HTML the way browsers do, malformed markup included.</summary>
public interface IPluginHtml
{
    /// <summary>The document's root element; <paramref name="baseUrl"/> resolves relative addresses in <see cref="IPluginElement.GetUrl"/>.</summary>
    IPluginElement Parse(string html, Uri? baseUrl = null);
}

/// <summary>An element of a parsed document.</summary>
public interface IPluginElement
{
    /// <summary>Lower case: <c>div</c>.</summary>
    string TagName { get; }

    /// <summary>The text of the element and its descendants, whitespace as in the document.</summary>
    string Text { get; }

    string InnerHtml { get; }

    /// <summary>The attribute's value, <c>null</c> when absent.</summary>
    string? GetAttribute(string name);

    /// <summary>The attribute (<c>href</c>, <c>src</c>) as an absolute http(s) address, resolved against the document's base; <c>null</c> when absent or not one.</summary>
    Uri? GetUrl(string attribute);

    /// <summary>The first descendant matching the CSS selector, or <c>null</c>.</summary>
    IPluginElement? QuerySelector(string selector);

    /// <summary>Every descendant matching the CSS selector, in document order.</summary>
    IReadOnlyList<IPluginElement> QuerySelectorAll(string selector);
}

/// <summary>HTTP as the host provides it: the plugin builds the request, the host sends it.</summary>
public interface IPluginHttp
{
    /// <summary>
    /// Sends the request and returns the answer, redirects followed. With <paramref name="streaming"/> the body is not
    /// buffered (images): dispose the response when done. Throws <see cref="SourceException"/> for a host outside the
    /// manifest, a transport failure or a timeout; a status other than 2xx is returned, not thrown.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool streaming, CancellationToken cancellationToken);
}

/// <summary>The plugin's settings, read live.</summary>
public interface IPluginSettings
{
    /// <summary>The value of a setting, or <c>null</c> when it is not set (empty counts as not set).</summary>
    string? Get(string key);
}

/// <summary>The host's log for the plugin: lines end up in Convy's log with the plugin's id.</summary>
public interface IPluginLog
{
    void Write(PluginLogLevel level, string message, Exception? exception = null);
}

public enum PluginLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}
