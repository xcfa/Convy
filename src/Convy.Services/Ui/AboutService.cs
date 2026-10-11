using System.Text.Json.Serialization;
using Convy.Services.Downloads;
using Convy.Services.Rules;
using Convy.Services.Security;
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

/// <summary>Version, runtime, paths and configuration for the About page.</summary>
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
    [property: JsonPropertyName("paths")] IReadOnlyList<ConfigurationSourceInfo> Paths,
    [property: JsonPropertyName("sources")] IReadOnlyList<ConfigurationSourceInfo> Sources,
    [property: JsonPropertyName("settings")] IReadOnlyList<SettingDto> Settings);

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
    private readonly UiOptions _ui;
    private readonly IOptionsMonitor<McpOptions> _mcp;
    private readonly TimeProvider _timeProvider;

    public AboutService(
        IConfigurationInspector configuration,
        IRulesDiagnostics rules,
        IDownloaderResolver downloaders,
        UiOptions ui,
        IOptionsMonitor<McpOptions> mcp,
        TimeProvider timeProvider)
    {
        _configuration = configuration;
        _rules = rules;
        _downloaders = downloaders;
        _ui = ui;
        _mcp = mcp;
        _timeProvider = timeProvider;
    }

    public AboutInfo Get()
    {
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
            Paths(uiAuth),
            _configuration.Sources,
            settings);
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
