using System.Text.Json.Serialization;
using Convy.Services.Downloads;
using Convy.Services.Media;
using Convy.Services.Rules;
using Convy.Services.Security;
using Convy.Sources;
using Microsoft.Extensions.Options;

namespace Convy.Services.Ui;

/// <summary>A configuration value with the source it comes from (the last source that sets it wins).</summary>
public sealed record ConfigurationValue(string Key, string? Value, string Source);

/// <summary>A configuration source in the order sources are applied.</summary>
/// <param name="Name">What it is: <c>configuration.yml</c>, <c>environment variables</c>, …</param>
/// <param name="Path">The file or directory it reads, when it has one.</param>
/// <param name="Exists">Whether that file or directory exists; <c>null</c> without a path.</param>
public sealed record ConfigurationSourceInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("exists")] bool? Exists);

/// <summary>Reads the host's configuration; implemented by the host, which knows its sources.</summary>
public interface IConfigurationInspector
{
    string EnvironmentName { get; }

    string ContentRoot { get; }

    /// <summary>Every configuration source, in the order they are applied (later ones win).</summary>
    IReadOnlyList<ConfigurationSourceInfo> Sources { get; }

    /// <summary>Every value set below the given top-level sections, with its source.</summary>
    IReadOnlyList<ConfigurationValue> Read(IReadOnlyCollection<string> sections);

    /// <summary>The effective value of one key, or <c>null</c>.</summary>
    string? this[string key] { get; }
}

/// <summary>Version, runtime, paths, search sources and configuration for the About page.</summary>
/// <param name="SearchSources">The search sources; <c>null</c> until they are first listed after a start.</param>
public sealed record AboutInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("informational_version")] string InformationalVersion,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("os")] string OperatingSystem,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("content_root")] string ContentRoot,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("now")] DateTimeOffset Now,
    [property: JsonPropertyName("ui_auth")] string UiAuth,
    [property: JsonPropertyName("mcp_enabled")] bool McpEnabled,
    [property: JsonPropertyName("downloaders")] IReadOnlyList<string> Downloaders,
    [property: JsonPropertyName("proxy")] AboutProxy Proxy,
    [property: JsonPropertyName("paths")] IReadOnlyList<ConfigurationSourceInfo> Paths,
    [property: JsonPropertyName("search_sources")] IReadOnlyList<AboutSearchSource>? SearchSources,
    [property: JsonPropertyName("search_warnings")] IReadOnlyList<string> SearchWarnings,
    [property: JsonPropertyName("configuration_sources")] IReadOnlyList<ConfigurationSourceInfo> ConfigurationSources,
    [property: JsonPropertyName("settings")] IReadOnlyList<SettingDto> Settings);

/// <summary>
/// The proxy of Convy's outgoing requests (catalogs, Prowlarr, slskd, qBittorrent, webhooks),
/// from the standard environment variables; credentials in it hidden.
/// </summary>
/// <param name="Http">For <c>http://</c> addresses: <c>HTTP_PROXY</c>, else <c>ALL_PROXY</c>.</param>
/// <param name="Https">For <c>https://</c> addresses: <c>HTTPS_PROXY</c>, else <c>ALL_PROXY</c>.</param>
/// <param name="NoProxy">Hosts reached directly: <c>NO_PROXY</c>.</param>
/// <param name="NoProxyIgnored">
/// Entries of <c>NO_PROXY</c> that .NET does not understand, so those hosts still go through the
/// proxy: it matches exact host names and addresses and <c>.domain</c> suffixes only, not
/// <c>*.domain</c>, <c>*</c> or address ranges such as <c>172.16.0.0/12</c>.
/// </param>
public sealed record AboutProxy(
    [property: JsonPropertyName("http")] string? Http,
    [property: JsonPropertyName("https")] string? Https,
    [property: JsonPropertyName("no_proxy")] string? NoProxy,
    [property: JsonPropertyName("no_proxy_ignored")] IReadOnlyList<string> NoProxyIgnored);

/// <summary>A search source with what downloads its results and which categories search it.</summary>
/// <param name="Status"><c>ok</c>, <c>error</c> or <c>disabled</c>.</param>
/// <param name="TimeoutSeconds">How long one request to it may take.</param>
/// <param name="Downloader">The downloader of its protocol; <c>null</c> when none is configured.</param>
public sealed record AboutSearchSource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("timeout_sec")] double TimeoutSeconds,
    [property: JsonPropertyName("downloader")] string? Downloader,
    [property: JsonPropertyName("categories")] IReadOnlyList<AboutSourceCategory> Categories);

/// <param name="Position">The source's place in the category's search order, from 1.</param>
/// <param name="Listed">The category lists the source; otherwise it searches every source.</param>
public sealed record AboutSourceCategory(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("listed")] bool Listed);

/// <param name="Hidden">The value (or a credential inside it) is hidden.</param>
public sealed record SettingDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("hidden")] bool Hidden);

/// <summary>
/// Collects what the About page shows. Only Convy's own settings are listed (not every
/// environment variable of the container), and secrets are hidden.
/// </summary>
public sealed class AboutService
{
    /// <summary>The top-level configuration sections Convy reads.</summary>
    public static readonly IReadOnlyList<string> Sections =
    [
        "ConnectionStrings", "Convy", "QBitTorrent", "UserSettings", "Prowlarr", "Slskd", "Opds", "Mcp", "Ui",
        "Categories", "Search", "Files", "Jobs", "Webhooks", "IpAccessControl", "TorrentMetadata", "Serilog",
    ];

    private readonly IConfigurationInspector _configuration;
    private readonly IRulesDiagnostics _rules;
    private readonly IDownloaderResolver _downloaders;
    private readonly ISourceRegistry _sources;
    private readonly CategoryCatalog _categories;
    private readonly IOptionsMonitor<SearchOptions> _search;
    private readonly UiOptions _ui;
    private readonly IOptionsMonitor<McpOptions> _mcp;
    private readonly TimeProvider _timeProvider;

    public AboutService(
        IConfigurationInspector configuration,
        IRulesDiagnostics rules,
        IDownloaderResolver downloaders,
        ISourceRegistry sources,
        CategoryCatalog categories,
        IOptionsMonitor<SearchOptions> search,
        UiOptions ui,
        IOptionsMonitor<McpOptions> mcp,
        TimeProvider timeProvider)
    {
        _configuration = configuration;
        _rules = rules;
        _downloaders = downloaders;
        _sources = sources;
        _categories = categories;
        _search = search;
        _ui = ui;
        _mcp = mcp;
        _timeProvider = timeProvider;
    }

    public AboutInfo Get()
    {
        // Never waits for Prowlarr, slskd or a catalog: right after a start the sources may not be listed yet.
        var (searchSources, searchWarnings) = _sources.TryGetSources(out var sources)
            ? SearchSources(sources)
            : (null, []);

        var settings = _configuration.Read(Sections)
            .Select(v =>
            {
                var value = ConfigurationMasking.Mask(v.Key, v.Value, out var hidden);
                return new SettingDto(v.Key, value, v.Source, hidden);
            })
            .OrderBy(s => SectionIndex(s.Key))
            .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var uiAuth = _ui.ResolveMode(out _) switch
        {
            UiMode.Oidc => "oidc",
            UiMode.Open => "none",
            _ => "off",
        };

        return new AboutInfo(
            AppInfo.Version,
            AppInfo.InformationalVersion,
            AppInfo.Commit,
            AppInfo.Runtime,
            AppInfo.OperatingSystem,
            AppInfo.Architecture,
            _configuration.EnvironmentName,
            _configuration.ContentRoot,
            AppInfo.StartedAt,
            _timeProvider.GetUtcNow(),
            uiAuth,
            !string.IsNullOrWhiteSpace(_mcp.CurrentValue.ApiKey),
            _downloaders.All.Select(d => d.Provider).ToList(),
            Proxy(),
            Paths(uiAuth),
            searchSources,
            searchWarnings,
            _configuration.Sources,
            settings);
    }

    /// <summary>
    /// Every source with the categories that search it, and what keeps a category or a source
    /// from working: a provider that cannot list its sources, a listed source that is unknown or
    /// disabled, a category left without sources, results that no downloader can fetch.
    /// </summary>
    private (IReadOnlyList<AboutSearchSource>? Sources, IReadOnlyList<string> Warnings) SearchSources(IReadOnlyList<IContentSource> sources)
    {
        var warnings = _sources.ProviderErrors
            .Select(e => $"Could not list the sources of {e.Provider}: {e.Message.TrimEnd('.')}." +
                         (e.KnownSources > 0 ? $" Its {e.KnownSources} last known source(s) are shown." : ""))
            .ToList();
        var usedBy = new Dictionary<string, List<AboutSourceCategory>>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in _categories.All)
        {
            var searched = CategorySources.Resolve(category, sources, out var skipped);
            var listed = category.Sources is { Count: > 0 };
            for (var i = 0; i < searched.Count; i++)
            {
                if (!usedBy.TryGetValue(searched[i].Id, out var list))
                    usedBy[searched[i].Id] = list = [];
                list.Add(new AboutSourceCategory(category.Id, i + 1, listed));
            }

            foreach (var id in skipped)
            {
                var known = sources.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                warnings.Add(known
                    ? $"Category '{category.Id}' lists source '{id}', which is disabled."
                    : $"Category '{category.Id}' lists source '{id}', which is not a known source.");
            }

            if (searched.Count == 0)
                warnings.Add($"Category '{category.Id}' has no source to search.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _search.CurrentValue.SourceTimeoutSeconds));
        var result = sources
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(s => new AboutSearchSource(
                s.Id,
                s.Name,
                ProtocolNames.ToName(s.Protocol),
                SourceStatusNames.ToName(s.Status),
                s.StatusMessage,
                (s.Timeout ?? timeout).TotalSeconds,
                _downloaders.All.FirstOrDefault(d => d.Protocol == s.Protocol)?.Provider,
                usedBy.GetValueOrDefault(s.Id) ?? []))
            .ToList();

        foreach (var protocol in sources.Where(s => s.Status != SourceStatus.Disabled).Select(s => s.Protocol).Distinct())
        {
            if (!_downloaders.All.Any(d => d.Protocol == protocol))
                warnings.Add($"No downloader handles {ProtocolNames.ToName(protocol)}: results of these sources can be found but not downloaded.");
        }

        return (result, warnings);
    }

    /// <summary>
    /// The proxy .NET's HTTP clients take from the environment: <c>HTTPS_PROXY</c> is for
    /// https:// addresses and <c>HTTP_PROXY</c> only for http:// ones, <c>ALL_PROXY</c> for both.
    /// </summary>
    private AboutProxy Proxy()
    {
        string? Read(string name) =>
            _configuration[name] is { Length: > 0 } value ? ConfigurationMasking.Mask("proxy", value.Trim(), out _) : null;

        var all = Read("ALL_PROXY");
        var noProxy = Read("NO_PROXY");
        var ignored = (noProxy ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.StartsWith('*') || entry.Contains('/'))
            .ToList();
        return new AboutProxy(Read("HTTP_PROXY") ?? all, Read("HTTPS_PROXY") ?? all, noProxy, ignored);
    }

    private List<ConfigurationSourceInfo> Paths(string uiAuth)
    {
        var connection = _configuration["ConnectionStrings:SQLite"];
        var paths = new List<ConfigurationSourceInfo>
        {
            File("database", AppInfo.DatabaseFile(connection)),
            File("rules", Path.GetFullPath(_rules.Path)),
        };

        if (uiAuth == "oidc")
        {
            paths.Add(Directory("sign-in keys", AppInfo.KeysDirectory(connection)));
        }

        if (_configuration["Opds:downloads_path"] is { Length: > 0 } books)
        {
            paths.Add(Directory("book downloads (OPDS)", books));
        }

        return paths;
    }

    private static ConfigurationSourceInfo File(string name, string? path) =>
        new(name, path, path is null ? null : System.IO.File.Exists(path));

    private static ConfigurationSourceInfo Directory(string name, string? path) =>
        new(name, path, path is null ? null : System.IO.Directory.Exists(path));

    private static int SectionIndex(string key)
    {
        var section = key.Split(':')[0];
        var index = Sections.ToList().FindIndex(s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }
}
