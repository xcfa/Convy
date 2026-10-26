using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Convy.Services.Media;
using Convy.Services.Settings;
using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shizuka.Sources.Hosting;
using Api = Shizuka.Sources.Abstractions;
using Content = Convy.Sources.Abstractions;

namespace Convy.Services.Extensions;

/// <summary>
/// The Sources page: the extensions installed and what each source is set to, the packages of the indexes, installing,
/// updating and removing packages (the extensions folder, loaded and unloaded at once), the sources' settings and the
/// indexes added there (saved in the settings database, over configuration.yml).
/// </summary>
public sealed class ExtensionAdminService
{
    /// <summary>What an index lists for Convy (<c>apps</c>).</summary>
    public const string App = "convy";

    private const string SettingsPath = "Extensions:settings:";
    private const string UiIndexesPath = "Extensions:ui_indexes:";

    private readonly ExtensionHost _host;
    private readonly ExtensionIndexClient _indexes;
    private readonly IUserSettingsService _settings;
    private readonly ISourceRegistry _registry;
    private readonly IOptionsMonitor<ExtensionsOptions> _options;
    private readonly ILogger<ExtensionAdminService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExtensionAdminService(
        ExtensionHost host, ExtensionIndexClient indexes, IUserSettingsService settings, ISourceRegistry registry,
        IOptionsMonitor<ExtensionsOptions> options, ILogger<ExtensionAdminService> logger)
    {
        _host = host;
        _indexes = indexes;
        _settings = settings;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    /// <summary>The extensions installed, the packages that failed and the indexes.</summary>
    public SourcesView Get()
    {
        var options = _options.CurrentValue;
        var listed = _registry.TryGetSources(out var known)
            ? known.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IContentSource>(StringComparer.OrdinalIgnoreCase);
        var failing = _registry.ProviderErrors.ToDictionary(e => e.Provider, StringComparer.OrdinalIgnoreCase);

        UnitDto Unit(ExtensionUnit unit, string kind, IReadOnlyList<ListedSourceDto>? sources = null)
        {
            var entry = options.For(unit.Id);
            var (status, message) = listed.TryGetValue(unit.Id, out var source)
                ? (StatusName(source.Status), source.StatusMessage)
                : failing.TryGetValue(unit.Id, out var error)
                    ? ("error", error.Message)
                    : (entry.Enabled ? (kind == "provider" ? "ok" : "unknown") : "disabled", (string?)null);
            var manifest = unit.Extension;
            return new UnitDto(
                unit.Id, manifest.Name, kind, entry.Enabled, entry.Enabled ? status : "disabled", message, entry.Proxy,
                entry.TimeoutSeconds, manifest.Domains, manifest.Nsfw,
                manifest.Settings.Select(s => Setting(s, entry.Values)).ToList(),
                manifest.Login is { } login ? new LoginDto(login.Description, login.Fields.Select(f => Setting(f, null)).ToList()) : null,
                sources);
        }

        var extensions = _host.Extensions.Select(e => new InstalledExtensionDto(
            e.Package.Package,
            e.Package.VersionText,
            Path.GetFileName(e.File),
            SourceApiRange.Format(e.Package.ApiVersion),
            e.Package.HostApis.TryGetValue(Content.ConvySourcesApi.AssemblyName, out var convy) ? SourceApiRange.Format(convy) : null,
            [
                .. e.Galleries.Select(g => Unit(g, "gallery")),
                .. e.Sources.Select(s => Unit(s, Kind(s.Protocol))),
                .. e.Providers.Select(p => Unit(p, "provider", _host.ListedBy(p.Id).Select(s =>
                {
                    var enabled = options.For(s.Id).Enabled;
                    var known = listed.GetValueOrDefault(s.Id);
                    return new ListedSourceDto(s.Id, s.Name, Kind(s.Protocol), enabled,
                        !enabled ? "disabled" : known is null ? "unknown" : StatusName(known.Status), known?.StatusMessage);
                }).ToList())),
            ])).ToList();

        return new SourcesView(
            options.Path,
            extensions,
            _host.Problems.Select(p => new ExtensionProblemDto(p.File, p.Message)).ToList(),
            [
                .. options.Indexes.Where(i => !string.IsNullOrWhiteSpace(i.Url)).Select(i => Index(i, "file")),
                .. options.UiIndexes.Where(i => !string.IsNullOrWhiteSpace(i.Url)).Select(i => Index(i, "ui")),
            ]);
    }

    /// <summary>The packages of every index that are for Convy, the newest of each; an index that fails is an error, the others still count.</summary>
    public async Task<AvailableView> AvailableAsync(bool fresh, CancellationToken cancellationToken)
    {
        var installed = _host.Extensions.ToDictionary(e => e.Package.Package, e => e.Package, StringComparer.OrdinalIgnoreCase);
        var packages = new List<AvailableExtensionDto>();
        var errors = new List<IndexErrorDto>();
        foreach (var index in AllIndexes())
        {
            IReadOnlyList<IndexEntry> entries;
            try
            {
                entries = await _indexes.GetIndexAsync(index.Url!.Trim(), Credentials(index), fresh, cancellationToken).ConfigureAwait(false);
            }
            catch (ExtensionIndexException ex)
            {
                errors.Add(new IndexErrorDto(index.Url!, ex.Message));
                continue;
            }

            foreach (var group in entries.Where(e => e.IsFor(App)).GroupBy(e => e.Package, StringComparer.OrdinalIgnoreCase))
            {
                var newest = group.OrderByDescending(e => e.Version).ToList();
                var entry = newest.FirstOrDefault(e => Incompatible(e) is null) ?? newest[0];
                var problem = Incompatible(entry);
                var current = installed.GetValueOrDefault(entry.Package);
                packages.Add(new AvailableExtensionDto(
                    index.Url!.Trim(),
                    entry.Package,
                    entry.Name,
                    ExtensionPackage.FormatVersion(entry.Version),
                    entry.Nsfw,
                    entry.Languages,
                    entry.Sources.Select(s => new AvailableSourceDto(s.Id, s.Name)).ToList(),
                    problem,
                    current?.VersionText,
                    problem is null && current is not null && entry.Version > current.Version));
            }
        }

        return new AvailableView(packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(), errors);
    }

    /// <summary>Installs or updates <paramref name="package"/> from the index at <paramref name="indexUrl"/>: the newest version Convy runs.</summary>
    public async Task<InstalledExtensionDto> InstallAsync(string? indexUrl, string? package, CancellationToken cancellationToken)
    {
        var index = AllIndexes().FirstOrDefault(i => string.Equals(i.Url!.Trim(), indexUrl?.Trim(), StringComparison.Ordinal))
                    ?? throw new ConvyRequestException($"There is no index {indexUrl}.");
        var credentials = Credentials(index);
        IReadOnlyList<IndexEntry> entries;
        try
        {
            entries = await _indexes.GetIndexAsync(index.Url!.Trim(), credentials, fresh: true, cancellationToken).ConfigureAwait(false);
        }
        catch (ExtensionIndexException ex)
        {
            throw new ConvyRequestException(ex.Message);
        }

        var candidates = entries
            .Where(e => e.IsFor(App) && string.Equals(e.Package, package?.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Version)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new ConvyRequestException($"The index has no package {package} for Convy.");
        }

        var entry = candidates.FirstOrDefault(e => Incompatible(e) is null)
                    ?? throw new ConvyRequestException(Incompatible(candidates[0])!);
        byte[] content;
        try
        {
            content = await _indexes.DownloadAsync(entry.File, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (ExtensionIndexException ex)
        {
            throw new ConvyRequestException(ex.Message);
        }

        var hash = ExtensionPackage.HashOf(content);
        if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConvyRequestException($"The package {entry.File} does not match the index: its SHA-256 is {hash}, the index says {entry.Sha256}.");
        }

        return await InstallPackageAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs a package file someone uploaded (one built by hand).</summary>
    public Task<InstalledExtensionDto> UploadAsync(byte[] content, CancellationToken cancellationToken)
    {
        if (content.Length == 0)
        {
            throw new ConvyRequestException("The file is empty.");
        }

        return InstallPackageAsync(content, cancellationToken);
    }

    /// <summary>Unloads a package and deletes its file; its sources' settings stay.</summary>
    public async Task RemoveAsync(string package, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = _host.Extensions.FirstOrDefault(e => string.Equals(e.Package.Package, package, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ConvyRequestException($"{package} is not installed.");
            _host.Unload(loaded.Package.Package);
            File.Delete(loaded.File);
            _logger.LogInformation("Removed extension {Package} ({File}).", loaded.Package.Package, loaded.File);
        }
        finally
        {
            _gate.Release();
        }

        await _registry.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a package file that could not be loaded.</summary>
    public Task RemoveBrokenAsync(string file, CancellationToken cancellationToken)
    {
        var problem = _host.Problems.FirstOrDefault(p => p.File == file) ?? throw new ConvyRequestException($"{file} is not a package that failed.");
        File.Delete(Path.Combine(_options.CurrentValue.Path, Path.GetFileName(problem.File)));
        _host.ForgetProblem(problem.File);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves what a source or a provider is set to: on or off, its proxy and timeout, its own settings by key (a value
    /// checked by the setting's kind; a secret or cookies left out are kept, an empty one is cleared). A source a
    /// provider lists is only switched on or off: its settings are the provider's.
    /// </summary>
    public async Task SaveSettingsAsync(string id, SourceSettingsInput input, CancellationToken cancellationToken)
    {
        var (unit, listed) = Find(id);
        var path = $"{SettingsPath}{unit?.Id ?? listed!.Id}:";
        var entries = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (input.Enabled is { } enabled)
        {
            entries[path + "enabled"] = enabled ? "true" : "false";
        }

        if (unit is null)
        {
            if (input.Proxy is not null || input.TimeoutSeconds is not null || input.Values is { Count: > 0 })
            {
                throw new ConvyRequestException($"{id} takes the settings of the provider that lists it; it can only be switched on or off.");
            }
        }
        else
        {
            if (input.Proxy is { } proxy)
            {
                if (proxy.Trim().Length > 0 && !(Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var proxyUrl) && proxyUrl.Scheme is "http" or "https" or "socks4" or "socks4a" or "socks5"))
                {
                    throw new ConvyRequestException($"The proxy '{proxy}' is not an address such as socks5://tor:9050.");
                }

                entries[path + "proxy"] = proxy.Trim();
            }

            if (input.TimeoutSeconds is { } timeout)
            {
                if (timeout is < 5 or > 3600)
                {
                    throw new ConvyRequestException("The timeout is 5 to 3600 seconds.");
                }

                entries[path + "timeout_sec"] = timeout.ToString(CultureInfo.InvariantCulture);
            }

            foreach (var (key, value) in input.Values ?? new Dictionary<string, string?>())
            {
                var setting = unit.Extension.Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
                              ?? throw new ConvyRequestException($"{unit.Extension.Name} has no setting {key}.");
                if (value is null)
                {
                    continue;
                }

                if (value.Trim().Length == 0)
                {
                    entries[$"{path}values:{setting.Key}"] = "";
                    continue;
                }

                entries[$"{path}values:{setting.Key}"] = SettingValues.Normalize(setting, value.Trim(), out var error)
                                                         ?? throw new ConvyRequestException($"{setting.Label}: {error}");
            }
        }

        await _settings.WriteAsync(entries, null, cancellationToken).ConfigureAwait(false);
        await _registry.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs a site's source in with what was typed into its login and saves the cookies it got; the fields are never stored.</summary>
    public async Task LoginAsync(string id, IReadOnlyDictionary<string, string>? fields, CancellationToken cancellationToken)
    {
        var gallery = _host.Extensions.SelectMany(e => e.Galleries).FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ConvyRequestException($"There is no site {id}.");
        var login = gallery.Extension.Login ?? throw new ConvyRequestException($"{gallery.Name} has no sign-in.");
        string cookies;
        try
        {
            cookies = await gallery.LoginAsync(fields ?? new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
        }
        catch (SourceException ex)
        {
            throw new ConvyRequestException(ex.Message);
        }

        var setting = gallery.Extension.Settings.First(s => s.Key == login.CookiesKey);
        var normalized = SettingValues.Normalize(setting, cookies.Trim(), out var error)
                         ?? throw new ConvyRequestException($"{gallery.Name} signed in, but its cookies are not usable: {error}");
        await _settings.WriteAsync(new Dictionary<string, string?> { [$"{SettingsPath}{gallery.Id}:values:{setting.Key}"] = normalized }, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Adds an index on the Sources page.</summary>
    public Task AddIndexAsync(IndexInput input, CancellationToken cancellationToken)
    {
        var url = input.Url?.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            throw new ConvyRequestException("The index is an http(s) address of an index.json.");
        }

        if (AllIndexes().Any(i => string.Equals(i.Url!.Trim(), url, StringComparison.Ordinal)))
        {
            throw new ConvyRequestException($"{url} is already there.");
        }

        var indexes = _options.CurrentValue.UiIndexes.Where(i => !string.IsNullOrWhiteSpace(i.Url)).ToList();
        indexes.Add(new ExtensionIndexOptions
        {
            Url = url,
            Username = Blank(input.Username),
            Password = Blank(input.Password),
            Token = Blank(input.Token),
        });
        return SaveIndexesAsync(indexes, cancellationToken);
    }

    /// <summary>Removes an index added on the Sources page.</summary>
    public Task RemoveIndexAsync(string url, CancellationToken cancellationToken)
    {
        var indexes = _options.CurrentValue.UiIndexes.Where(i => !string.IsNullOrWhiteSpace(i.Url)).ToList();
        if (indexes.RemoveAll(i => string.Equals(i.Url!.Trim(), url.Trim(), StringComparison.Ordinal)) == 0)
        {
            throw new ConvyRequestException($"{url} was not added here; an index of configuration.yml is removed there.");
        }

        return SaveIndexesAsync(indexes, cancellationToken);
    }

    private async Task SaveIndexesAsync(List<ExtensionIndexOptions> indexes, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (index, n) in indexes.Select((i, n) => (i, n)))
        {
            entries[$"{UiIndexesPath}{n}:url"] = index.Url!.Trim();
            foreach (var (key, value) in new[] { ("username", index.Username), ("password", index.Password), ("token", index.Token) })
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    entries[$"{UiIndexesPath}{n}:{key}"] = value;
                }
            }
        }

        await _settings.WriteAsync(entries, UiIndexesPath, cancellationToken).ConfigureAwait(false);
        _indexes.Invalidate();
    }

    private async Task<InstalledExtensionDto> InstallPackageAsync(byte[] content, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        ExtensionPackage package;
        try
        {
            package = ExtensionPackage.Read(content, options.Trusted, [Content.ConvySourcesApi.AssemblyName]);
        }
        catch (ExtensionPackageException ex)
        {
            throw new ConvyRequestException(ex.Message);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(options.Path);
            var file = Path.Combine(options.Path, package.Package + (content.AsSpan().StartsWith("PK"u8) ? ".zip" : ".dll"));
            var staged = file + ".new";
            await File.WriteAllBytesAsync(staged, content, cancellationToken).ConfigureAwait(false);

            // The version installed stays aside until the new one loads, and comes back if it does not.
            string? kept = null;
            var old = _host.Extensions.FirstOrDefault(e => string.Equals(e.Package.Package, package.Package, StringComparison.OrdinalIgnoreCase));
            if (old is not null)
            {
                _host.Unload(old.Package.Package);
                kept = old.File + ".old";
                File.Move(old.File, kept, overwrite: true);
            }

            File.Move(staged, file, overwrite: true);
            _host.Load(file);
            if (_host.Problems.FirstOrDefault(p => p.File == Path.GetFileName(file)) is { } problem)
            {
                File.Delete(file);
                _host.ForgetProblem(problem.File);
                if (old is not null)
                {
                    File.Move(kept!, old.File, overwrite: true);
                    _host.Load(old.File);
                }

                throw new ConvyRequestException(problem.Message);
            }

            if (kept is not null)
            {
                File.Delete(kept);
            }

            _logger.LogInformation("Installed extension {Package} {Version}.", package.Package, package.VersionText);
        }
        finally
        {
            _gate.Release();
        }

        await _registry.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
        return Get().Extensions.First(e => string.Equals(e.Package, package.Package, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Why Convy cannot run what an index lists; <c>null</c> — it can.</summary>
    private static string? Incompatible(IndexEntry entry)
    {
        if (!SourceApiRange.Default.Supports(entry.ApiVersion))
        {
            return SourceApiRange.Default.IncompatibleMessage(entry.ApiVersion);
        }

        foreach (var (api, version) in entry.Requires)
        {
            if (!string.Equals(api, Content.ConvySourcesApi.AssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return $"It is built against {api}, which Convy does not provide.";
            }

            if (!ExtensionHost.ContentApiRange.Supports(version))
            {
                return $"{api}: {ExtensionHost.ContentApiRange.IncompatibleMessage(version)}";
            }
        }

        return null;
    }

    private (ExtensionUnit? Unit, PackagedSource? Listed) Find(string id)
    {
        var extensions = _host.Extensions;
        if (extensions.SelectMany(e => e.Units).FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase)) is { } unit)
        {
            return (unit, null);
        }

        return extensions.SelectMany(e => e.Providers).SelectMany(p => _host.ListedBy(p.Id))
                   .FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) is { } listed
            ? (null, listed)
            : throw new ConvyRequestException($"There is no source {id}.");
    }

    private IEnumerable<ExtensionIndexOptions> AllIndexes()
    {
        var options = _options.CurrentValue;
        return options.Indexes.Concat(options.UiIndexes).Where(i => !string.IsNullOrWhiteSpace(i.Url));
    }

    private static IndexDto Index(ExtensionIndexOptions index, string source) =>
        new(index.Url!.Trim(), source,
            !string.IsNullOrWhiteSpace(index.Token) ? "bearer" : !string.IsNullOrWhiteSpace(index.Username) ? "basic" : "none");

    private static IndexCredentials? Credentials(ExtensionIndexOptions index)
    {
        var origin = new Uri(index.Url!.Trim());
        if (!string.IsNullOrWhiteSpace(index.Token))
        {
            return new IndexCredentials(origin, "Authorization", "Bearer " + index.Token.Trim());
        }

        return string.IsNullOrWhiteSpace(index.Username)
            ? null
            : new IndexCredentials(origin, "Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{index.Username.Trim()}:{index.Password}")));
    }

    private static SourceSettingDto Setting(Api.SourceSettingDefinition setting, IReadOnlyDictionary<string, string?>? values)
    {
        var value = values?.FirstOrDefault(v => string.Equals(v.Key, setting.Key, StringComparison.OrdinalIgnoreCase)).Value;
        var secret = setting.Kind is Api.SourceSettingKind.Secret or Api.SourceSettingKind.Cookies;
        return new SourceSettingDto(
            setting.Key, setting.Label, setting.Kind.ToString().ToLowerInvariant(), setting.Description, setting.Options, setting.Default,
            secret ? null : value, !string.IsNullOrWhiteSpace(value));
    }

    private static string Kind(Protocol protocol) => protocol switch
    {
        Protocol.Torrent => "torrent",
        Protocol.Soulseek => "soulseek",
        _ => "files",
    };

    private static string StatusName(SourceStatus status) => status switch
    {
        SourceStatus.Ok => "ok",
        SourceStatus.Disabled => "disabled",
        _ => "error",
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The Sources page.</summary>
public sealed record SourcesView(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("extensions")] IReadOnlyList<InstalledExtensionDto> Extensions,
    [property: JsonPropertyName("problems")] IReadOnlyList<ExtensionProblemDto> Problems,
    [property: JsonPropertyName("indexes")] IReadOnlyList<IndexDto> Indexes);

/// <summary>An installed package and what Convy runs of it.</summary>
public sealed record InstalledExtensionDto(
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("api")] string Api,
    [property: JsonPropertyName("convy_api")] string? ConvyApi,
    [property: JsonPropertyName("units")] IReadOnlyList<UnitDto> Units);

/// <summary>
/// A source or a provider of a package: <c>gallery</c> (a site), <c>torrent</c>, <c>soulseek</c>, <c>files</c>, or
/// <c>provider</c> with the sources it lists.
/// </summary>
public sealed record UnitDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("proxy")] string? Proxy,
    [property: JsonPropertyName("timeout_sec")] double TimeoutSeconds,
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
    [property: JsonPropertyName("nsfw")] bool Nsfw,
    [property: JsonPropertyName("settings")] IReadOnlyList<SourceSettingDto> Settings,
    [property: JsonPropertyName("login")] LoginDto? Login,
    [property: JsonPropertyName("sources")] IReadOnlyList<ListedSourceDto>? Sources);

/// <summary>A source a provider lists.</summary>
public sealed record ListedSourceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>A setting of a source; a secret's or cookies' value is never sent back, only whether it is set.</summary>
public sealed record SourceSettingDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("options")] IReadOnlyList<string> Options,
    [property: JsonPropertyName("default")] string? Default,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("set")] bool Set);

/// <summary>A site's sign-in: the fields to fill in.</summary>
public sealed record LoginDto(
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("fields")] IReadOnlyList<SourceSettingDto> Fields);

public sealed record ExtensionProblemDto(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("message")] string Message);

/// <summary>An index: from configuration.yml (<c>file</c>) or added on the page (<c>ui</c>).</summary>
public sealed record IndexDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("auth")] string Auth);

public sealed record AvailableView(
    [property: JsonPropertyName("extensions")] IReadOnlyList<AvailableExtensionDto> Extensions,
    [property: JsonPropertyName("errors")] IReadOnlyList<IndexErrorDto> Errors);

/// <summary>A package of an index; <c>incompatible</c> says why Convy cannot run it.</summary>
public sealed record AvailableExtensionDto(
    [property: JsonPropertyName("index")] string Index,
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("nsfw")] bool Nsfw,
    [property: JsonPropertyName("languages")] IReadOnlyList<string> Languages,
    [property: JsonPropertyName("sources")] IReadOnlyList<AvailableSourceDto> Sources,
    [property: JsonPropertyName("incompatible")] string? Incompatible,
    [property: JsonPropertyName("installed")] string? Installed,
    [property: JsonPropertyName("update")] bool Update);

public sealed record AvailableSourceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record IndexErrorDto(
    [property: JsonPropertyName("index")] string Index,
    [property: JsonPropertyName("message")] string Message);

/// <summary>What the page saves for a source; a field left out (<c>null</c>) is kept.</summary>
public sealed record SourceSettingsInput(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("proxy")] string? Proxy,
    [property: JsonPropertyName("timeout_sec")] double? TimeoutSeconds,
    [property: JsonPropertyName("values")] IReadOnlyDictionary<string, string?>? Values);

public sealed record IndexInput(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("token")] string? Token);

public sealed record InstallInput(
    [property: JsonPropertyName("index")] string? Index,
    [property: JsonPropertyName("package")] string? Package);

public sealed record LoginInput(
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string>? Fields);
