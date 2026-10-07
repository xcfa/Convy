using System.Net;
using System.Net.Http.Headers;
using Banned.Qbittorrent;
using Banned.Qbittorrent.Exceptions;
using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Sync;
using Banned.Qbittorrent.Models.Torrent;
using Microsoft.Extensions.Options;

namespace Convy.Services.Downloaders.QBittorrent;

/// <summary>
/// <see cref="IQBittorrentApi"/> over the qBittorrent Web API. Reads and start/stop go
/// through the Banned.Qbittorrent client; adding torrents and setting file priorities are
/// sent directly, because the library always sends a save path (<c>/download</c> by default,
/// overriding the category's) and names the file-id parameter <c>ids</c> instead of <c>id</c>.
/// Both sessions log in lazily and log in again after the session is rejected.
/// </summary>
/// <remarks>
/// The client library does not accept cancellation tokens, so its calls are awaited with
/// <see cref="Task.WaitAsync(CancellationToken)"/>: cancellation stops the wait, the HTTP
/// request itself finishes in the background on its own timeout.
/// </remarks>
public sealed class QBittorrentApi : IQBittorrentApi, IDisposable
{
    private readonly QBitTorrentConnectionSettings _settings;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly HttpClient _http;

    private QBittorrentClient? _client;
    private volatile bool _loggedIn;

    public QBittorrentApi(IOptions<QBitTorrentConnectionSettings> settings)
    {
        _settings = settings.Value;
        _http = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            BaseAddress = new Uri(_settings.Url.TrimEnd('/') + "/api/v2/"),
        };
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

    public Task AddTorrentFileAsync(byte[] torrentFile, string? category, bool stopped, CancellationToken cancellationToken) =>
        AddAsync(form =>
        {
            var file = new ByteArrayContent(torrentFile);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
            form.Add(file, "torrents", "convy.torrent");
        }, category, stopped, cancellationToken);

    public Task AddMagnetAsync(string magnet, string? category, bool stopped, CancellationToken cancellationToken) =>
        AddAsync(form => form.Add(new StringContent(magnet), "urls"), category, stopped, cancellationToken);

    public async Task SetFilesPriorityAsync(
        string hash, IReadOnlyList<int> fileIndexes, EnumTorrentFilePriority priority, CancellationToken cancellationToken)
    {
        var value = priority switch
        {
            EnumTorrentFilePriority.DoNotDownload => "0",
            EnumTorrentFilePriority.Normal => "1",
            EnumTorrentFilePriority.High => "6",
            _ => "7",
        };

        using var response = await PostAsync("torrents/filePrio", () => new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hash"] = hash,
            ["id"] = string.Join('|', fileIndexes),
            ["priority"] = value,
        }), cancellationToken).ConfigureAwait(false);
    }

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
        _http.Dispose();
        _connectGate.Dispose();
        _loginGate.Dispose();
    }

    /// <summary>
    /// <c>/torrents/add</c> without a save path, so qBittorrent applies the category's
    /// (or its default) path. Both <c>stopped</c> (API ≥ 2.11) and <c>paused</c> are sent.
    /// </summary>
    private async Task AddAsync(Action<MultipartFormDataContent> addSource, string? category, bool stopped, CancellationToken cancellationToken)
    {
        using var response = await PostAsync("torrents/add", () =>
        {
            var form = new MultipartFormDataContent();
            addSource(form);
            if (!string.IsNullOrEmpty(category))
            {
                form.Add(new StringContent(category), "category");
            }

            var flag = stopped ? "true" : "false";
            form.Add(new StringContent(flag), "stopped");
            form.Add(new StringContent(flag), "paused");
            return form;
        }, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("qBittorrent rejected the torrent.");
        }
    }

    /// <summary>Posts to the Web API with Convy's own session; logs in again once on 403.</summary>
    private async Task<HttpResponseMessage> PostAsync(string path, Func<HttpContent> content, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

            using var body = content();
            var response = await _http.PostAsync(path, body, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
            {
                response.Dispose();
                _loggedIn = false;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                response.Dispose();
                throw new HttpRequestException(
                    $"qBittorrent {path} failed with {(int)response.StatusCode}: {detail}".TrimEnd(' ', ':'),
                    null,
                    response.StatusCode);
            }

            return response;
        }
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn)
        {
            return;
        }

        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loggedIn)
            {
                return;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = _settings.Username,
                ["password"] = _settings.Password ?? string.Empty,
            });
            using var response = await _http.PostAsync("auth/login", form, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"qBittorrent login failed ({(int)response.StatusCode}).");
            }

            _loggedIn = true;
        }
        finally
        {
            _loginGate.Release();
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
