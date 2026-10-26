using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Convy.Services.Captcha;
using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shizuka.Sources.Hosting;
using Api = Shizuka.Sources.Abstractions;

namespace Convy.Services.Extensions;

/// <summary>An extension package that is loaded: its file, the package as read and its sources that Convy runs.</summary>
public sealed record LoadedExtension(string File, ExtensionPackage Package, IReadOnlyList<ExtensionSource> Sources);

/// <summary>A package in the extensions folder that could not be loaded.</summary>
public sealed record ExtensionProblem(string File, string Message);

/// <summary>
/// Loads the Shizuka source extensions of the extensions folder at startup the way Shizuka does (the package audit, the
/// API range, a collectible load context each, the manifests checked) and serves the sources that download
/// (<see cref="Api.SourceCapabilities.Download"/>) as gallery sources. Sources that only know metadata, and providers,
/// are Shizuka's business and are left out. A package that cannot be loaded, or whose source ids are taken, is reported
/// (<see cref="Problems"/>, the log) and skipped; the others run. A disabled source
/// (<c>extensions.settings.&lt;id&gt;.enabled: false</c>) is not listed.
/// </summary>
public sealed class ExtensionHost : ISourceProvider, IDisposable
{
    public const string ProviderName = "extensions";

    private readonly IOptionsMonitor<ExtensionsOptions> _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<ExtensionHost> _logger;
    private readonly Func<string?, HttpMessageHandler> _handlers;
    private readonly ConcurrentDictionary<string, SourceTransport> _transports = new(StringComparer.Ordinal);
    private readonly List<(LoadedExtension Extension, ExtensionLoadContext Context)> _loaded = [];
    private readonly List<ExtensionProblem> _problems = [];

    public ExtensionHost(IOptionsMonitor<ExtensionsOptions> options, IOptionsMonitor<CaptchaOptions> captcha, TimeProvider time, ILoggerFactory loggers)
        : this(options, captcha, time, loggers, CreateHandler, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <param name="handlers">The transport for a source's proxy setting; tests pass a fake site.</param>
    /// <param name="captchaHandler">The transport to the service of checks; tests pass a fake service.</param>
    public ExtensionHost(IOptionsMonitor<ExtensionsOptions> options, IOptionsMonitor<CaptchaOptions> captcha, TimeProvider time, ILoggerFactory loggers,
        Func<string?, HttpMessageHandler> handlers, HttpMessageHandler captchaHandler)
    {
        _options = options;
        Captcha = captcha;
        Time = time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<ExtensionHost>();
        _handlers = handlers;
        CaptchaHttp = new HttpClient(captchaHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Name => ProviderName;

    public IReadOnlyList<LoadedExtension> Extensions => _loaded.Select(l => l.Extension).ToList();

    public IReadOnlyList<ExtensionProblem> Problems => _problems;

    /// <summary>The domains of every loaded source: the UI's tab passes their checks.</summary>
    public IEnumerable<string> Domains => _loaded.SelectMany(l => l.Extension.Sources).SelectMany(s => s.Extension.Domains);

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

        var files = Directory.EnumerateFiles(path)
            .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        foreach (var file in files)
        {
            Load(file);
        }

        _logger.LogInformation("{Count} extension(s) loaded from {Path}: {Extensions}.", _loaded.Count, path,
            string.Join(", ", _loaded.Select(l => $"{l.Extension.Package.Package} {l.Extension.Package.VersionText} ({string.Join(", ", l.Extension.Sources.Select(s => s.Id))})")));
    }

    /// <summary>Loads one package file; a failure is an <see cref="ExtensionProblem"/>, never an exception.</summary>
    public void Load(string file)
    {
        try
        {
            var package = ExtensionPackage.Read(File.ReadAllBytes(file), Options.Trusted);
            if (!SourceApiRange.Default.Supports(package.ApiVersion))
            {
                throw new ExtensionLoadException(SourceApiRange.Default.IncompatibleMessage(package.ApiVersion));
            }

            if (_loaded.Any(l => string.Equals(l.Extension.Package.Package, package.Package, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ExtensionLoadException($"{package.Package} is already loaded from another file.");
            }

            Activate(package, file);
        }
        catch (Exception ex) when (ex is ExtensionPackageException or ExtensionLoadException or IOException or UnauthorizedAccessException)
        {
            _problems.Add(new ExtensionProblem(Path.GetFileName(file), ex.Message));
            _logger.LogError(ex, "Extension {File} was not loaded: {Message}", Path.GetFileName(file), ex.Message);
        }
    }

    public Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var options = Options;
        var sources = _loaded.SelectMany(l => l.Extension.Sources).Where(s => options.For(s.Id).Enabled).ToList<IContentSource>();
        return Task.FromResult<IReadOnlyList<IContentSource>>(sources);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Activate(ExtensionPackage package, string file)
    {
        var context = new ExtensionLoadContext(package);
        try
        {
            var types = ExtensionTypes.Create(context.LoadMain(), package.Package);
            foreach (var provider in types.Providers)
            {
                _logger.LogInformation("{Package}: {Provider} is a provider of sources for Shizuka; Convy leaves it out.", package.Package, provider.Manifest.Id);
            }

            var taken = _loaded.SelectMany(l => l.Extension.Sources).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sources = new List<ExtensionSource>();
            foreach (var source in types.Sources)
            {
                var manifest = source.Manifest;
                if (!manifest.Capabilities.HasFlag(Api.SourceCapabilities.Download))
                {
                    _logger.LogInformation("{Package}: {Source} does not download releases; Convy leaves it out.", package.Package, manifest.Id);
                    continue;
                }

                if (!taken.Add(manifest.Id))
                {
                    throw new ExtensionLoadException($"{package.Package}: the source id '{manifest.Id}' is already taken.");
                }

                sources.Add(new ExtensionSource(source, package.Package, this, _loggers.CreateLogger($"Convy.Extensions.{manifest.Id}")));
            }

            if (sources.Count == 0)
            {
                context.Unload();
                _logger.LogInformation("Extension {File} has no source that downloads; not kept.", Path.GetFileName(file));
                return;
            }

            _loaded.Add((new LoadedExtension(file, package, sources), context));
            _logger.LogInformation("Loaded extension {File}: {Sources}.", Path.GetFileName(file), string.Join(", ", sources.Select(s => s.Id)));
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

    public void Dispose()
    {
        foreach (var (_, context) in _loaded)
        {
            context.Unload();
        }

        _loaded.Clear();
        foreach (var transport in _transports.Values)
        {
            transport.Dispose();
        }

        _transports.Clear();
        CaptchaHttp.Dispose();
    }
}
