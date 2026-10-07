using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Convy.Sources.Slskd;

/// <summary>Connection to slskd (<c>SLSKD__URL</c>, <c>SLSKD__APIKEY</c>).</summary>
public sealed class SlskdOptions
{
    public const string SectionName = "Slskd";

    public string? Url { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>
    /// slskd's downloads directory as seen inside Convy. Defaults to slskd's own
    /// <c>directories.downloads</c>, which only works when both mount it at the same path.
    /// </summary>
    public string? DownloadsPath { get; set; }

    /// <summary>slskd ends a search after this long without new responses.</summary>
    public double SearchInactivitySeconds { get; set; } = 8;

    /// <summary>A search still running after this long is stopped and its responses are used.</summary>
    public double SearchMaxSeconds { get; set; } = 15;

    /// <summary>Peer responses collected per search.</summary>
    public int ResponseLimit { get; set; } = 100;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record SlskdSearch(Guid Id, string? State, bool IsComplete, int ResponseCount);

public sealed record SlskdFile(string Filename, long Size, string? Extension = null, int? BitRate = null, int? Length = null);

public sealed record SlskdResponse(
    string Username, IReadOnlyList<SlskdFile>? Files, bool HasFreeUploadSlot, int QueueLength, long UploadSpeed);

/// <summary>A browsed directory; file names are bare (without the directory).</summary>
public sealed record SlskdDirectory(string Name, IReadOnlyList<SlskdFile>? Files);

public sealed record SlskdTransfer
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string Filename { get; init; }
    public long Size { get; init; }

    /// <summary>Flags string such as <c>Queued, Remotely</c> or <c>Completed, Succeeded</c>.</summary>
    public string? State { get; init; }

    public long BytesTransferred { get; init; }
    public double AverageSpeed { get; init; }
    public string? Exception { get; init; }

    /// <summary>Set while an automatic retry is scheduled (slskd 0.26+).</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }
}

public sealed record SlskdUserTransfers(string Username, IReadOnlyList<SlskdTransferDirectory>? Directories);

public sealed record SlskdTransferDirectory(string Directory, IReadOnlyList<SlskdTransfer>? Files);

/// <summary>A file to enqueue: full remote name and the size the peer reported.</summary>
public sealed record SlskdEnqueueFile(string Filename, long Size);

/// <summary>
/// HTTP client for the slskd API (<c>/api/v0</c>, <c>X-API-Key</c>). slskd accepts one search
/// request at a time, so starting searches is serialised here; polling is not.
/// </summary>
public sealed class SlskdClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _searchStartGate = new(1, 1);

    public SlskdClient(HttpClient http, SlskdOptions options)
    {
        _http = http;
        Options = options;
    }

    public SlskdOptions Options { get; }

    public async Task<SlskdSearch> StartSearchAsync(string text, CancellationToken cancellationToken)
    {
        var request = new
        {
            id = Guid.NewGuid(),
            searchText = text,
            searchTimeout = (int)(Options.SearchInactivitySeconds * 1000), // milliseconds
            responseLimit = Options.ResponseLimit,
            fileLimit = 10_000,
            filterResponses = true,
        };

        await _searchStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendAsync<SlskdSearch>(HttpMethod.Post, "searches", request, cancellationToken).ConfigureAwait(false)
                   ?? throw new SourceException(SourceErrorKind.Error, "slskd returned no search.");
        }
        finally
        {
            _searchStartGate.Release();
        }
    }

    public async Task<SlskdSearch> GetSearchAsync(Guid id, CancellationToken cancellationToken) =>
        await SendAsync<SlskdSearch>(HttpMethod.Get, $"searches/{id}?includeResponses=false", null, cancellationToken).ConfigureAwait(false)
        ?? throw new SourceException(SourceErrorKind.Error, "The search disappeared from slskd.");

    /// <summary>Stops a running search; its responses are kept.</summary>
    public Task StopSearchAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Put, $"searches/{id}", null, cancellationToken);

    /// <summary>Responses of a search; slskd stores them only once the search has ended.</summary>
    public async Task<IReadOnlyList<SlskdResponse>> GetSearchResponsesAsync(Guid id, CancellationToken cancellationToken) =>
        await SendAsync<List<SlskdResponse>>(HttpMethod.Get, $"searches/{id}/responses", null, cancellationToken).ConfigureAwait(false) ?? [];

    public Task DeleteSearchAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Delete, $"searches/{id}", null, cancellationToken);

    /// <summary>The files of one remote directory (the first entry is the directory itself).</summary>
    public async Task<IReadOnlyList<SlskdDirectory>> BrowseDirectoryAsync(string username, string directory, CancellationToken cancellationToken) =>
        await SendAsync<List<SlskdDirectory>>(
            HttpMethod.Post, $"users/{Uri.EscapeDataString(username)}/directory", new { directory }, cancellationToken)
            .ConfigureAwait(false) ?? [];

    /// <summary>Queues downloads from one user; returns the transfers slskd accepted.</summary>
    public async Task<IReadOnlyList<SlskdTransfer>> EnqueueAsync(
        string username, IReadOnlyList<SlskdEnqueueFile> files, CancellationToken cancellationToken)
    {
        var response = await SendAsync<EnqueueResponse>(
            HttpMethod.Post, $"transfers/downloads/{Uri.EscapeDataString(username)}", files, cancellationToken).ConfigureAwait(false);
        return response?.Enqueued ?? [];
    }

    public async Task<IReadOnlyList<SlskdUserTransfers>> GetDownloadsAsync(CancellationToken cancellationToken) =>
        await SendAsync<List<SlskdUserTransfers>>(HttpMethod.Get, "transfers/downloads", null, cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>The downloads from one user, or <c>null</c> when there are none.</summary>
    public Task<SlskdUserTransfers?> GetUserDownloadsAsync(string username, CancellationToken cancellationToken) =>
        SendAsync<SlskdUserTransfers>(HttpMethod.Get, $"transfers/downloads/{Uri.EscapeDataString(username)}", null, cancellationToken, notFoundIsNull: true);

    /// <summary>Cancels a transfer without removing it or its data.</summary>
    public Task CancelDownloadAsync(string username, Guid id, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Delete, $"transfers/downloads/{Uri.EscapeDataString(username)}/{id}?remove=false", null, cancellationToken);

    /// <summary>slskd's own downloads directory (<c>directories.downloads</c>).</summary>
    public async Task<string?> GetDownloadsDirectoryAsync(CancellationToken cancellationToken)
    {
        using var document = await SendAsync<JsonDocument>(HttpMethod.Get, "options", null, cancellationToken).ConfigureAwait(false);
        return document is not null
               && document.RootElement.TryGetProperty("directories", out var directories)
               && directories.TryGetProperty("downloads", out var downloads)
            ? downloads.GetString()
            : null;
    }

    /// <summary>Whether slskd is logged in to the Soulseek network.</summary>
    public async Task<bool> IsLoggedInAsync(CancellationToken cancellationToken)
    {
        var state = await SendAsync<ServerState>(HttpMethod.Get, "server", null, cancellationToken).ConfigureAwait(false);
        return state?.IsLoggedIn == true;
    }

    public void Dispose() => _searchStartGate.Dispose();

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-API-Key", Options.ApiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException(SourceErrorKind.Error, $"slskd is unreachable: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new SourceException(SourceErrorKind.AuthFailed, "slskd rejected the API key.");
            }

            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new SourceException(
                    SourceErrorKind.Error,
                    $"slskd {method} {path.Split('?')[0]} failed with {(int)response.StatusCode}" +
                    (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail.Trim()}"));
            }

            if (typeof(T) == typeof(object) || response.Content.Headers.ContentLength == 0)
            {
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record EnqueueResponse(List<SlskdTransfer>? Enqueued);

    private sealed record ServerState(bool IsLoggedIn, string? State);
}
