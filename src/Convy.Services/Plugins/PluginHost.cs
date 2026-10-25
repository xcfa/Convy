using System.Runtime.CompilerServices;
using Convy.Services.Captcha;
using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Plugins;

/// <summary>A plugin that is loaded and running.</summary>
/// <param name="Package">The package it came from.</param>
/// <param name="File">The file in the plugins folder.</param>
/// <param name="Manifest">What it says about itself.</param>
/// <param name="Sources">Its sources, as created once at load.</param>
public sealed record LoadedPlugin(PluginPackage Package, string File, PluginManifest Manifest, IReadOnlyList<IContentSource> Sources);

/// <summary>A package in the plugins folder that could not be loaded.</summary>
public sealed record PluginProblem(string File, string Message);

/// <summary>
/// Loads the plugin packages of the plugins folder at startup, each into its own <see cref="PluginLoadContext"/>, and
/// serves their sources as one <see cref="ISourceProvider"/>. A package that cannot be read, is built against another
/// API major, has no <see cref="SourcePlugin"/> or whose source ids collide is reported (<see cref="Problems"/>, the
/// log) and skipped; the others run. A disabled plugin (<c>plugins.settings.&lt;id&gt;.enabled: false</c>) lists no sources.
/// </summary>
public sealed class PluginHost : ISourceProvider, IDisposable
{
    public const string ProviderName = "plugins";

    private readonly IOptionsMonitor<PluginsOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<PluginHost> _logger;
    private readonly Func<string?, HttpMessageHandler> _handlers;
    private readonly CaptchaService? _captcha;
    private readonly List<(LoadedPlugin Plugin, PluginContext Context, PluginLoadContext LoadContext)> _loaded = [];
    private readonly List<PluginProblem> _problems = [];

    public PluginHost(IOptionsMonitor<PluginsOptions> options, TimeProvider timeProvider, ILoggerFactory loggers, CaptchaService captcha)
        : this(options, timeProvider, loggers, PluginHttp.CreateHandler, captcha)
    {
    }

    /// <param name="handlers">The transport for a plugin's proxy setting; tests pass a fake.</param>
    /// <param name="captcha">The service of checks the plugins' requests hand bot checks to.</param>
    public PluginHost(IOptionsMonitor<PluginsOptions> options, TimeProvider timeProvider, ILoggerFactory loggers, Func<string?, HttpMessageHandler> handlers,
        CaptchaService? captcha = null)
    {
        _captcha = captcha;
        _options = options;
        _timeProvider = timeProvider;
        _loggers = loggers;
        _logger = loggers.CreateLogger<PluginHost>();
        _handlers = handlers;
    }

    public string Name => ProviderName;

    public IReadOnlyList<LoadedPlugin> Plugins => _loaded.Select(l => l.Plugin).ToList();

    public IReadOnlyList<PluginProblem> Problems => _problems;

    /// <summary>The folder's packages (<c>*.dll</c>, <c>*.zip</c>), by name. A missing folder is simply empty.</summary>
    public void LoadAll()
    {
        var path = _options.CurrentValue.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _logger.LogInformation("No plugins folder at {Path}.", path);
            return;
        }

        var files = Directory.EnumerateFiles(path)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        foreach (var file in files)
        {
            Load(file);
        }

        _logger.LogInformation("{Count} plugin(s) loaded from {Path}: {Plugins}.", _loaded.Count, path,
            string.Join(", ", _loaded.Select(l => $"{l.Plugin.Manifest.Id} {l.Plugin.Package.VersionText}")));
    }

    /// <summary>Loads one package file; a failure is a <see cref="PluginProblem"/>, never an exception.</summary>
    public void Load(string file)
    {
        try
        {
            var package = PluginPackage.Read(File.ReadAllBytes(file), _options.CurrentValue.Trusted);
            if (!package.IsCompatible)
            {
                throw new PluginPackageException(
                    $"{package.Package} {package.VersionText} needs plugin API {package.ApiVersion.ToString(2)}; Convy has {SourcesApi.Version}.");
            }

            if (_loaded.Any(l => string.Equals(l.Plugin.Package.Package, package.Package, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PluginPackageException($"{package.Package} is already loaded from another file.");
            }

            Activate(package, file);
            _logger.LogInformation("Loaded plugin {File}.", Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is PluginPackageException or IOException or UnauthorizedAccessException)
        {
            _problems.Add(new PluginProblem(Path.GetFileName(file), ex.Message));
            _logger.LogError(ex, "Plugin {File} was not loaded: {Message}", Path.GetFileName(file), ex.Message);
        }
    }

    public Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = _loaded.Where(l => l.Context.Enabled).SelectMany(l => l.Plugin.Sources).ToList();
        return Task.FromResult<IReadOnlyList<IContentSource>>(sources);
    }

    /// <summary>The plugin whose sources include <paramref name="sourceId"/>, or <c>null</c>.</summary>
    public LoadedPlugin? PluginOf(string sourceId) =>
        _loaded.FirstOrDefault(l => l.Plugin.Sources.Any(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase))).Plugin;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Activate(PluginPackage package, string file)
    {
        var loadContext = new PluginLoadContext(package);
        PluginContext? context = null;
        try
        {
            var assembly = loadContext.LoadMain();
            var types = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(SourcePlugin).IsAssignableFrom(t)).ToList();
            if (types.Count != 1)
            {
                throw new PluginPackageException(types.Count == 0
                    ? $"{package.Package} has no class deriving from {nameof(SourcePlugin)}."
                    : $"{package.Package} has several classes deriving from {nameof(SourcePlugin)}: {string.Join(", ", types.Select(t => t.Name))}.");
            }

            var plugin = (SourcePlugin)(Activator.CreateInstance(types[0]) ?? throw new PluginPackageException($"{types[0].Name} could not be created."));
            var manifest = plugin.Manifest ?? throw new PluginPackageException($"{package.Package} has no manifest.");
            if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id != manifest.Id.ToLowerInvariant().Trim())
            {
                throw new PluginPackageException($"{package.Package}: the manifest id must be a non-empty lower-case string.");
            }

            if (_loaded.Any(l => string.Equals(l.Plugin.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PluginPackageException($"{package.Package}: a plugin with the id '{manifest.Id}' is already loaded.");
            }

            var settings = _options.CurrentValue.For(manifest.Id);
            context = new PluginContext(manifest, _options, _handlers(settings.Proxy), _timeProvider, _loggers.CreateLogger($"Convy.Plugins.{manifest.Id}"), _captcha);
            var sources = plugin.CreateSources(context) ?? [];
            var taken = _loaded.SelectMany(l => l.Plugin.Sources).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source.Id) || !taken.Add(source.Id))
                {
                    throw new PluginPackageException($"{package.Package}: the source id '{source.Id}' is empty or already taken.");
                }
            }

            _loaded.Add((new LoadedPlugin(package, file, manifest, sources), context, loadContext));
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            context?.Dispose();
            loadContext.Unload();
            throw new PluginPackageException($"{package.Package} cannot be loaded: {ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message}");
        }
        catch (Exception ex) when (ex is not PluginPackageException)
        {
            context?.Dispose();
            loadContext.Unload();
            throw new PluginPackageException($"{package.Package} failed while starting: {ex.Message}");
        }
        catch
        {
            context?.Dispose();
            loadContext.Unload();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var (_, context, loadContext) in _loaded)
        {
            context.Dispose();
            loadContext.Unload();
        }

        _loaded.Clear();
    }
}
