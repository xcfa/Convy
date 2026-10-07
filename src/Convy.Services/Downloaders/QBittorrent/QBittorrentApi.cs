using Banned.Qbittorrent;
using Banned.Qbittorrent.Exceptions;
using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Sync;
using Banned.Qbittorrent.Models.Torrent;
using Microsoft.Extensions.Options;

namespace Convy.Services.Downloaders.QBittorrent;

/// <summary>
/// <see cref="IQBittorrentApi"/> over the Banned.Qbittorrent client. Logs in lazily on
/// first use and logs in again after the session is rejected.
/// </summary>
/// <remarks>
/// The client library does not accept cancellation tokens, so calls are awaited with
/// <see cref="Task.WaitAsync(CancellationToken)"/>: cancellation stops the wait, the HTTP
/// request itself finishes in the background on its own timeout.
/// </remarks>
public sealed class QBittorrentApi : IQBittorrentApi, IDisposable
{
    private readonly QBitTorrentConnectionSettings _settings;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private QBittorrentClient? _client;

    public QBittorrentApi(IOptions<QBitTorrentConnectionSettings> settings)
    {
        _settings = settings.Value;
    }

    public Task<MainData> GetMainDataAsync(int rid, CancellationToken cancellationToken) =>
        CallAsync(async c => await c.Sync.GetMainData(rid).ConfigureAwait(false)
                             ?? throw new InvalidOperationException("qBittorrent returned an empty sync response."),
            cancellationToken);

    public Task<TorrentInfo?> GetTorrentInfoAsync(string hash, CancellationToken cancellationToken) =>
        CallAsync<TorrentInfo?>(async c =>
        {
            var info = await c.Torrent.GetTorrentInfo(hash).ConfigureAwait(false);
            if (info is not null)
            {
                info.Hash ??= hash;
            }

            return info;
        }, cancellationToken);

    public Task<IReadOnlyList<TorrentFileInfo>?> GetTorrentFilesAsync(string hash, CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<TorrentFileInfo>?>(async c =>
        {
            try
            {
                return await c.Torrent.GetTorrentFiles(hash).ConfigureAwait(false);
            }
            catch (QbittorrentNotFoundException)
            {
                return null;
            }
        }, cancellationToken);

    public async Task AddTorrentFileAsync(byte[] torrentFile, string? category, bool stopped, CancellationToken cancellationToken)
    {
        // The client library uploads .torrent files by path only.
        var path = Path.Combine(Path.GetTempPath(), $"convy-{Guid.NewGuid():N}.torrent");
        await File.WriteAllBytesAsync(path, torrentFile, cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await CallAsync(
                c => c.Torrent.AddTorrent(filePaths: [path], category: category, stopped: stopped, paused: stopped),
                cancellationToken).ConfigureAwait(false);
            EnsureAccepted(response);
        }
        finally
        {
            // No async delete exists; removing a small temp file is a metadata-only operation.
            File.Delete(path);
        }
    }

    public async Task AddMagnetAsync(string magnet, string? category, bool stopped, CancellationToken cancellationToken)
    {
        var response = await CallAsync(
            c => c.Torrent.AddTorrent(urls: [magnet], category: category, stopped: stopped, paused: stopped),
            cancellationToken).ConfigureAwait(false);
        EnsureAccepted(response);
    }

    public Task SetFilesPriorityAsync(string hash, IReadOnlyList<int> fileIndexes, EnumTorrentFilePriority priority, CancellationToken cancellationToken) =>
        CallAsync(c => c.Torrent.SetFilesPriority(hash, fileIndexes.ToList(), priority), cancellationToken);

    public Task StopAsync(string hash, CancellationToken cancellationToken) =>
        CallAsync(c => c.Torrent.PauseTorrent(hash), cancellationToken);

    public Task StartAsync(string hash, CancellationToken cancellationToken) =>
        CallAsync(c => c.Torrent.ResumeTorrent(hash), cancellationToken);

    public Task<string> GetDefaultSavePathAsync(CancellationToken cancellationToken) =>
        CallAsync(c => c.Application.GetDefaultSavePath(), cancellationToken);

    public Task<IReadOnlyList<TorrentCategory>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<TorrentCategory>>(
            async c => await c.Torrent.GetAllCategories().ConfigureAwait(false) ?? [],
            cancellationToken);

    public void Dispose()
    {
        _client?.Dispose();
        _connectGate.Dispose();
    }

    private static void EnsureAccepted(string? response)
    {
        if (response is not null && response.Contains("Fails", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("qBittorrent rejected the torrent.");
        }
    }

    private async Task CallAsync(Func<QBittorrentClient, Task> call, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await call(client).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (QbittorrentException ex) when (IsSessionRejected(ex))
        {
            Invalidate(client);
            throw;
        }
    }

    private async Task<T> CallAsync<T>(Func<QBittorrentClient, Task<T>> call, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call(client).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (QbittorrentException ex) when (IsSessionRejected(ex))
        {
            Invalidate(client);
            throw;
        }
    }

    private static bool IsSessionRejected(QbittorrentException ex) =>
        ex is QbittorrentUnauthorizedException or QbittorrentForbiddenException or QbittorrentLoginFailedException;

    /// <summary>Drops a client whose session was rejected so the next call logs in again.</summary>
    private void Invalidate(QBittorrentClient client) =>
        Interlocked.CompareExchange(ref _client, null, client);

    private async Task<QBittorrentClient> GetClientAsync(CancellationToken cancellationToken)
    {
        var existing = Volatile.Read(ref _client);
        if (existing is not null)
        {
            return existing;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var client = await QBittorrentClient
                .Create(_settings.Url, _settings.Username, _settings.Password ?? string.Empty)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await client.Authentication.Login().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _client = client;
            return client;
        }
        finally
        {
            _connectGate.Release();
        }
    }
}
