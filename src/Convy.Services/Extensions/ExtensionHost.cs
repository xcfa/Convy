using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Convy.Services.Captcha;
using Convy.Sources;
using Convy.Sources.Torrents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shizuka.Sources.Hosting;
using Api = Shizuka.Sources.Abstractions;
using Content = Convy.Sources.Abstractions;

namespace Convy.Services.Extensions;

/// <summary>
/// An extension package that is loaded: its file, the package as read and what Convy runs of it: the sites' sources
/// that download (<paramref name="Galleries"/>), the sources and providers of Convy's own API.
/// </summary>
public sealed record LoadedExtension(
    string File,
    ExtensionPackage Package,
    IReadOnlyList<ExtensionSource> Galleries,
    IReadOnlyList<PackagedSource> Sources,
    IReadOnlyList<PackagedProvider> Providers)
{
    /// <summary>The ids of its sources and providers.</summary>
    public IEnumerable<string> Ids => Galleries.Select(g => g.Id).Concat(Sources.Select(s => s.Id)).Concat(Providers.Select(p => p.Id));

    /// <summary>Every unit of it: galleries, sources, providers.</summary>
    public IEnumerable<ExtensionUnit> Units => Galleries.Cast<ExtensionUnit>().Concat(Sources).Concat(Providers);
}

/// <summary>A package in the extensions folder that could not be loaded.</summary>
public sealed record ExtensionProblem(string File, string Message);

/// <summary>
/// Loads the source extensions of the extensions folder the way Shizuka does (the package audit, the API range, a
/// collectible load context each, the manifests checked). A site's package (the shared
/// <c>Shizuka.Sources.Abstractions</c>) gives its sources that download (<see cref="Api.SourceCapabilities.Download"/>)
/// as gallery sources; sources that only know metadata, and providers of sites, are Shizuka's business and are left
/// out. A package of Convy's own API (<c>Convy.Sources.Abstractions</c>) gives its <see cref="Content.ContentSource"/>s
/// and <see cref="Content.ContentSourceProvider"/>s. A package that cannot be loaded, or whose ids are taken, is
/// reported (<see cref="Problems"/>, the log) and skipped; the others run. A disabled source or provider
/// (<c>extensions.settings.&lt;id&gt;.enabled: false</c>) is not listed.
/// </summary>
public sealed class ExtensionHost : ISourceProvider, ISourceProviderGroup, IDisposable
{
    public const string ProviderName = "extensions";

    private readonly IOptionsMonitor<ExtensionsOptions> _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<ExtensionHost> _logger;
    private readonly Func<string?, HttpMessageHandler> _handlers;
    private readonly ConcurrentDictionary<string, SourceTransport> _transports = new(StringComparer.Ordinal);
    private readonly List<(LoadedExtension Extension, ExtensionLoadContext Context)> _loaded = [];
    private readonly List<ExtensionProblem> _problems = [];
    private readonly ConcurrentDictionary<string, IReadOnlyList<PackagedSource>> _listed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The API of Convy's own sources, as the extensions reference it: the versions this server runs.</summary>
    public static SourceApiRange ContentApiRange { get; } = new(new Version(2, 0), Content.ConvySourcesApi.Version);

    /// <summary>The kinds of source Convy's own API defines.</summary>
    private static readonly IReadOnlyList<HostSourceType> ContentTypes =
    [
        new(typeof(Content.ContentSource), o => ((Content.ContentSource)o).Manifest),
        new(typeof(Content.ContentSourceProvider), o => ((Content.ContentSourceProvider)o).Manifest, IsProvider: true),
    ];

    public ExtensionHost(IOptionsMonitor<ExtensionsOptions> options, IOptionsMonitor<CaptchaOptions> captcha, ITorrentMetadataService torrents,
        TimeProvider time, ILoggerFactory loggers)
        : this(options, captcha, time, loggers, CreateHandler, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, torrents)
    {
    }

    /// <param name="handlers">The transport for a source's proxy setting; tests pass a fake site.</param>
    /// <param name="captchaHandler">The transport to the service of checks; tests pass a fake service.</param>
    /// <param name="torrents">Reads the torrents of the sources; none — a torrent's files cannot be listed.</param>
    public ExtensionHost(IOptionsMonitor<ExtensionsOptions> options, IOptionsMonitor<CaptchaOptions> captcha, TimeProvider time, ILoggerFactory loggers,
        Func<string?, HttpMessageHandler> handlers, HttpMessageHandler captchaHandler, ITorrentMetadataService? torrents = null)
    {
        _options = options;
        Captcha = captcha;
        Torrents = torrents ?? new NoTorrents();
        Time = time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<ExtensionHost>();
        _handlers = handlers;
        CaptchaHttp = new HttpClient(captchaHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Name => ProviderName;

    public IReadOnlyList<LoadedExtension> Extensions
    {
        get
        {
            lock (_loaded)
            {
                return _loaded.Select(l => l.Extension).ToList();
            }
        }
    }

    public IReadOnlyList<ExtensionProblem> Problems
    {
        get
        {
            lock (_loaded)
            {
                return _problems.ToList();
            }
        }
    }

    /// <summary>This host's own sources, then each provider of an extension: the registry lists them one by one.</summary>
    public IReadOnlyList<ISourceProvider> Providers => [this, ..Extensions.SelectMany(e => e.Providers)];

    /// <summary>The domains of every loaded site: the UI's tab passes their checks.</summary>
    public IEnumerable<string> Domains => Extensions.SelectMany(e => e.Galleries).SelectMany(s => s.Extension.Domains);

    /// <summary>A source of Convy's own API by id, as last listed (a provider's) or loaded.</summary>
    public PackagedSource? FindSource(string id) =>
        Extensions.SelectMany(e => e.Sources).Concat(_listed.Values.SelectMany(l => l))
            .FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The sources a provider listed last; none before its first listing.</summary>
    public IReadOnlyList<PackagedSource> ListedBy(string providerId) => _listed.GetValueOrDefault(providerId) ?? [];

    /// <summary>Reads the torrents of the sources.</summary>
    internal ITorrentMetadataService Torrents { get; }

    internal ExtensionsOptions Options => _options.CurrentValue;

    internal IOptionsMonitor<CaptchaOptions> Captcha { get; }

    internal TimeProvider Time { get; }

    internal Api.IHtmlParser Html { get; } = new HtmlParser();

    /// <summary>Plain HTTP to the service of checks.</summary>
    internal HttpClient CaptchaHttp { get; }

    /// <summary>The connections of the sources with one proxy (none: <c>null</c>), made once.</summary>
    internal SourceTransport Transport(string? proxy)
    {
        var key = string.IsNullOrWhiteSpace(proxy) ? "" : proxy.Trim();
        return _transports.GetOrAdd(key, k =>
        {
            var transport = new SourceTransport(_handlers(k.Length == 0 ? null : k));
            transport.Client.DefaultRequestHeaders.UserAgent.Clear();
            transport.Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Convy", "1.0"));
            return transport;
        });
    }

    /// <summary>The transport of a proxy setting: no automatic redirects (the source's HTTP follows them and checks each address), no automatic cookies (each source has its own jar).</summary>
    public static SocketsHttpHandler CreateHandler(string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(new Uri(proxy.Trim()));
            handler.UseProxy = true;
        }

        return handler;
    }

    /// <summary>The folder's packages (<c>*.zip</c>, <c>*.dll</c>), by name. A missing folder is simply empty.</summary>
    public void LoadAll()
    {
        var path = Options.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _logger.LogInformation("No extensions folder at {Path}.", path);
            return;
        }

        RecoverInstalls(path);
        var files = Directory.EnumerateFiles(path)
            .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        foreach (var file in files)
        {
            Load(file);
        }

        var loaded = Extensions;
        _logger.LogInformation("{Count} extension(s) loaded from {Path}: {Extensions}.", loaded.Count, path,
            string.Join(", ", loaded.Select(e => $"{e.Package.Package} {e.Package.VersionText} ({string.Join(", ", e.Ids)})")));
    }

    /// <summary>
    /// What an install cut short (Convy stopped mid-way) left behind: a version set aside (<c>.old</c>) whose package file
    /// is missing comes back; a staged one (<c>.new</c>) is dropped.
    /// </summary>
    private void RecoverInstalls(string path)
    {
        foreach (var kept in Directory.EnumerateFiles(path, "*.old"))
        {
            var file = kept[..^".old".Length];
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(kept);
                }
                else
                {
                    File.Move(kept, file);
                    _logger.LogWarning("Restored {File}, set aside by an install that did not finish.", Path.GetFileName(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not restore {File}: {Message}", Path.GetFileName(file), ex.Message);
            }
        }

        foreach (var staged in Directory.EnumerateFiles(path, "*.new"))
        {
            try
            {
                File.Delete(staged);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not remove {File}: {Message}", Path.GetFileName(staged), ex.Message);
            }
        }
    }

    /// <summary>Loads one package file; a failure is an <see cref="ExtensionProblem"/>, never an exception.</summary>
    public void Load(string file)
    {
        try
        {
            var package = ExtensionPackage.Read(File.ReadAllBytes(file), Options.Trusted, [Content.ConvySourcesApi.AssemblyName]);
            if (!SourceApiRange.Default.Supports(package.ApiVersion))
            {
                throw new ExtensionLoadException(SourceApiRange.Default.IncompatibleMessage(package.ApiVersion));
            }

            if (package.HostApis.TryGetValue(Content.ConvySourcesApi.AssemblyName, out var convyApi) && !ContentApiRange.Supports(convyApi))
            {
                throw new ExtensionLoadException($"{Content.ConvySourcesApi.AssemblyName}: {ContentApiRange.IncompatibleMessage(convyApi)}");
            }

            lock (_loaded)
            {
                if (_loaded.Any(l => string.Equals(l.Extension.Package.Package, package.Package, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ExtensionLoadException($"{package.Package} is already loaded from another file.");
                }

                Activate(package, file);
            }
        }
        catch (Exception ex) when (ex is ExtensionPackageException or ExtensionLoadException or IOException or UnauthorizedAccessException)
        {
            lock (_loaded)
            {
                _problems.RemoveAll(p => p.File == Path.GetFileName(file));
                _problems.Add(new ExtensionProblem(Path.GetFileName(file), ex.Message));
            }

            _logger.LogError(ex, "Extension {File} was not loaded: {Message}", Path.GetFileName(file), ex.Message);
        }
    }

    /// <summary>Unloads the package of <paramref name="package"/>'s name: its sources go with it. False — none such is loaded.</summary>
    public bool Unload(string package)
    {
        lock (_loaded)
        {
            var index = _loaded.FindIndex(l => string.Equals(l.Extension.Package.Package, package, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            var (extension, context) = _loaded[index];
            _loaded.RemoveAt(index);
            foreach (var provider in extension.Providers)
            {
                _listed.TryRemove(provider.Id, out _);
            }

            context.Unload();
            _logger.LogInformation("Unloaded extension {Package}.", extension.Package.Package);
            return true;
        }
    }

    /// <summary>Forgets the problem of a file that is no longer there.</summary>
    public void ForgetProblem(string file)
    {
        lock (_loaded)
        {
            _problems.RemoveAll(p => p.File == file);
        }
    }

    /// <summary>The galleries and Convy's own sources of the extensions (a provider's are its own listing): those enabled, each asked whether it can be used.</summary>
    public async Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var options = Options;
        var extensions = Extensions;
        var sources = extensions.SelectMany(e => e.Sources).Where(s => options.For(s.Id).Enabled).ToList();
        await Task.WhenAll(sources.Select(s => s.CheckAsync(cancellationToken))).ConfigureAwait(false);
        return extensions.SelectMany(e => e.Galleries).Where(s => options.For(s.Id).Enabled)
            .Concat<IContentSource>(sources)
            .ToList();
    }

    /// <summary>The sources a provider lists now: those enabled, each asked whether it can be used.</summary>
    internal async Task<IReadOnlyList<IContentSource>> ListAsync(PackagedProvider provider, CancellationToken cancellationToken)
    {
        var options = Options;
        if (!options.For(provider.Id).Enabled)
        {
            _listed.TryRemove(provider.Id, out _);
            return [];
        }

        var taken = Extensions.SelectMany(e => e.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, listed) in _listed.Where(l => !string.Equals(l.Key, provider.Id, StringComparison.OrdinalIgnoreCase)))
        {
            taken.UnionWith(listed.Select(s => s.Id));
        }

        var sources = await provider.ListAsync(taken, cancellationToken).ConfigureAwait(false);
        lock (_loaded)
        {
            // Unloaded while it listed: its sources go with it, not into the list.
            if (!_loaded.Any(l => l.Extension.Providers.Contains(provider)))
            {
                return [];
            }

            _listed[provider.Id] = sources;
        }

        var enabled = sources.Where(s => options.For(s.Id).Enabled).ToList();
        await Task.WhenAll(enabled.Select(s => s.CheckAsync(cancellationToken))).ConfigureAwait(false);
        return enabled;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Activate(ExtensionPackage package, string file)
    {
        var context = new ExtensionLoadContext(package, [typeof(Content.ContentSource).Assembly]);
        try
        {
            var types = ExtensionTypes.Create(context.LoadMain(), package.Package, ContentTypes);
            foreach (var provider in types.Providers)
            {
                _logger.LogInformation("{Package}: {Provider} is a provider of sites for Shizuka; Convy leaves it out.", package.Package, provider.Manifest.Id);
            }

            var taken = _loaded.SelectMany(l => l.Extension.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var listed in _listed.Values)
            {
                taken.UnionWith(listed.Select(s => s.Id));
            }

            void Take(string id)
            {
                if (!taken.Add(id))
                {
                    throw new ExtensionLoadException($"{package.Package}: the source id '{id}' is already taken.");
                }
            }

            var galleries = new List<ExtensionSource>();
            foreach (var source in types.Sources)
            {
                var manifest = source.Manifest;
                if (!manifest.Capabilities.HasFlag(Api.SourceCapabilities.Download))
                {
                    _logger.LogInformation("{Package}: {Source} does not download releases; Convy leaves it out.", package.Package, manifest.Id);
                    continue;
                }

                Take(manifest.Id);
                galleries.Add(new ExtensionSource(source, package.Package, this, _loggers.CreateLogger($"Convy.Extensions.{manifest.Id}")));
            }

            var sources = new List<PackagedSource>();
            var providers = new List<PackagedProvider>();
            foreach (var hosted in types.Hosted)
            {
                Take(hosted.Manifest.Id);
                switch (hosted.Instance)
                {
                    case Content.ContentSource source:
                        sources.Add(new PackagedSource(source, package.Package, this, _loggers.CreateLogger($"Convy.Extensions.{hosted.Manifest.Id}")));
                        break;
                    case Content.ContentSourceProvider provider:
                        providers.Add(new PackagedProvider(provider, package.Package, this, _loggers));
                        break;
                }
            }

            if (galleries.Count + sources.Count + providers.Count == 0)
            {
                // A problem rather than silence: the page shows the file and can remove it.
                throw new ExtensionLoadException($"{package.Package} has nothing Convy runs: no source that downloads releases.");
            }

            var extension = new LoadedExtension(file, package, galleries, sources, providers);
            _loaded.Add((extension, context));
            _problems.RemoveAll(p => p.File == Path.GetFileName(file));
            _logger.LogInformation("Loaded extension {File}: {Sources}.", Path.GetFileName(file), string.Join(", ", extension.Ids));
        }
        catch (Exception ex) when (ex is not ExtensionLoadException)
        {
            context.Unload();
            throw new ExtensionLoadException($"{package.Package} failed while starting: {ex.Message}");
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    /// <summary>No reader of torrents: a host without one cannot list a torrent's files.</summary>
    private sealed class NoTorrents : ITorrentMetadataService
    {
        public TorrentMetadata Parse(byte[] torrentFile) => throw new InvalidDataException("Torrents cannot be read here.");

        public Task<TorrentMetadata> FetchAsync(string magnet, CancellationToken cancellationToken) =>
            throw new SourceException(SourceErrorKind.Error, "Torrents cannot be read here.");
    }

    public void Dispose()
    {
        lock (_loaded)
        {
            foreach (var (_, context) in _loaded)
            {
                context.Unload();
            }

            _loaded.Clear();
            _listed.Clear();
        }

        foreach (var transport in _transports.Values)
        {
            transport.Dispose();
        }

        _transports.Clear();
        CaptchaHttp.Dispose();
    }
}
