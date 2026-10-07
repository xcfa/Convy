using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Banned.Qbittorrent;
using Banned.Qbittorrent.Exceptions;
using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Sync;
using Banned.Qbittorrent.Models.Torrent;
using Microsoft.Extensions.Options;

namespace Convy.Services.Downloaders.QBittorrent;

/// <summary>
/// <see cref="IQBittorrentApi"/> over the qBittorrent Web API. Reads and start/stop go
/// through the Banned.Qbittorrent client; adding torrents, setting file priorities and
/// reading categories are sent directly, because the library always sends a save path
/// (<c>/download</c> by default, overriding the category's), names the file-id parameter
/// <c>ids</c> instead of <c>id</c>, and expects the categories as an array while qBittorrent
/// returns an object keyed by name.
/// Both sessions log in lazily and log in again after the session is rejected.
/// </summary>
/// <remarks>
/// The client library does not accept cancellation tokens. Its calls are bounded by
/// <see cref="LibraryTimeout"/> and always awaited to the end; cancellation is honoured
/// right after, so no request is ever left running unobserved.
/// </remarks>
public sealed class QBittorrentApi : IQBittorrentApi, IDisposable
{
    /// <summary>Upper bound of one call through the client library.</summary>
    private static readonly TimeSpan LibraryTimeout = TimeSpan.FromSeconds(30);

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

        using var response = await SendAsync(HttpMethod.Post, "torrents/filePrio", () => new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hash"] = hash,
            ["id"] = string.Join('|', fileIndexes),
            ["priority"] = value,
        }), cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAsync(string hash, CancellationToken cancellationToken) =>
        CallAsync(c => c.Torrent.DeleteTorrent(hash, deleteFile: false), cancellationToken);

    public Task StopAsync(string hash, CancellationToken cancellationToken) =>
        CallAsync(c => c.Torrent.PauseTorrent(hash), cancellationToken);

    public Task StartAsync(string hash, CancellationToken cancellationToken) =>
        CallAsync(c => c.Torrent.ResumeTorrent(hash), cancellationToken);

    public Task<string> GetDefaultSavePathAsync(CancellationToken cancellationToken) =>
        CallAsync(c => c.Application.GetDefaultSavePath(), cancellationToken);

    public async Task<IReadOnlyList<TorrentCategory>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "torrents/categories", null, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        // { "Movies": { "name": "Movies", "savePath": "/data/movies" }, ... }
        return document.RootElement.ValueKind != JsonValueKind.Object
            ? []
            : document.RootElement.EnumerateObject()
                .Select(category => new TorrentCategory
                {
                    Name = category.Value.TryGetProperty("name", out var name) ? name.GetString() ?? category.Name : category.Name,
                    SavePath = category.Value.TryGetProperty("savePath", out var path) ? path.GetString() ?? string.Empty : string.Empty,
                })
                .ToList();
    }

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
        using var response = await SendAsync(HttpMethod.Post, "torrents/add", () =>
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

    /// <summary>Calls the Web API with Convy's own session; logs in again once on 403.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, Func<HttpContent>? content, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(method, path) { Content = content?.Invoke() };
            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

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
            await call(client).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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
            var result = await call(client).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
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
    private void Invalidate(QBittorrentClient client)
    {
        if (Interlocked.CompareExchange(ref _client, null, client) == client)
        {
            client.Dispose();
        }
    }

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
                .Create(_settings.Url, _settings.Username, _settings.Password ?? string.Empty, timeout: LibraryTimeout)
                .ConfigureAwait(false);

            try
            {
                await client.Authentication.Login().ConfigureAwait(false);
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
