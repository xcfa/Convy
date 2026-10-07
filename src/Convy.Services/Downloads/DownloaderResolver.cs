using Convy.Sources;

namespace Convy.Services.Downloads;

/// <summary>Picks the downloader for a protocol and enumerates the configured downloaders.</summary>
public interface IDownloaderResolver
{
    /// <summary>All configured downloaders, in registration order.</summary>
    IReadOnlyList<IDownloader> All { get; }

    /// <summary>
    /// Returns the downloader for <paramref name="protocol"/>. Throws
    /// <see cref="InvalidOperationException"/> when none is configured.
    /// </summary>
    IDownloader Resolve(Protocol protocol);

    /// <summary>Returns the downloader with the given provider name, or <c>null</c>.</summary>
    IDownloader? FindByProvider(string provider);
}

/// <inheritdoc />
public sealed class DownloaderResolver : IDownloaderResolver
{
    public DownloaderResolver(IEnumerable<IDownloader> downloaders)
    {
        All = downloaders.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<IDownloader> All { get; }

    /// <inheritdoc />
    public IDownloader Resolve(Protocol protocol) =>
        All.FirstOrDefault(d => d.Protocol == protocol)
        ?? throw new InvalidOperationException($"No downloader is configured for protocol '{protocol}'.");

    /// <inheritdoc />
    public IDownloader? FindByProvider(string provider) =>
        All.FirstOrDefault(d => string.Equals(d.Provider, provider, StringComparison.OrdinalIgnoreCase));
}
