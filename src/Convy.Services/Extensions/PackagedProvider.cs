using Convy.Sources;
using Microsoft.Extensions.Logging;
using Shizuka.Sources.Hosting;
using Content = Convy.Sources.Abstractions;

namespace Convy.Services.Extensions;

/// <summary>
/// A provider of Convy's own API (<see cref="Content.ContentSourceProvider"/>: Prowlarr, whose indexers are the
/// sources). Its settings (<c>extensions.settings.&lt;provider id&gt;</c>) are those of every source it lists; it is
/// asked for them each time the sources are listed.
/// </summary>
public sealed class PackagedProvider : ExtensionUnit, ISourceProvider
{
    private readonly Content.ContentSourceProvider _provider;
    private readonly ILoggerFactory _loggers;

    public PackagedProvider(Content.ContentSourceProvider provider, string package, ExtensionHost host, ILoggerFactory loggers)
        : base(provider.Manifest, package, host, loggers.CreateLogger($"Convy.Extensions.{provider.Manifest.Id}"))
    {
        _provider = provider;
        _loggers = loggers;
    }

    public string Name => _provider.Manifest.Name;

    /// <summary>The registry knows the provider by its id.</summary>
    string ISourceProvider.Name => Id;

    /// <summary>The sources it lists now that are enabled, each asked whether it can be used.</summary>
    public Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken) => Host.ListAsync(this, cancellationToken);

    /// <summary>
    /// The provider's sources now. A source whose manifest is bad, or whose id is the provider's or one of
    /// <paramref name="taken"/>, is left out with a warning.
    /// </summary>
    public async Task<IReadOnlyList<PackagedSource>> ListAsync(ISet<string> taken, CancellationToken cancellationToken)
    {
        var listed = await CallAsync(async (context, ct) => await _provider.GetSourcesAsync(context, ct).ConfigureAwait(false) ?? [],
            cancellationToken).ConfigureAwait(false);
        var sources = new List<PackagedSource>();
        foreach (var source in listed)
        {
            if (source?.Manifest is not { } manifest)
            {
                Logger.LogWarning("{Provider} listed a source without a manifest; left out.", Id);
                continue;
            }

            // Its settings are the provider's: a Url setting there gives it its host.
            if (ManifestRules.Check(manifest with { Settings = _provider.Manifest.Settings }) is { } problem)
            {
                Logger.LogWarning("{Provider} listed {Source}, whose manifest is bad: {Problem}", Id, manifest.Id, problem);
                continue;
            }

            if (string.Equals(manifest.Id, Id, StringComparison.OrdinalIgnoreCase) || !taken.Add(manifest.Id))
            {
                Logger.LogWarning("{Provider} listed {Source}, whose id is taken; left out.", Id, manifest.Id);
                continue;
            }

            sources.Add(new PackagedSource(source, Package, Host, _loggers.CreateLogger($"Convy.Extensions.{manifest.Id}"), this));
        }

        return sources;
    }
}
