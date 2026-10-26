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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

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

    /// <summary>The batch the transfer was queued in (slskd 0.26+), if any.</summary>
    public Guid? BatchId { get; init; }
}

public sealed record SlskdUserTransfers(string Username, IReadOnlyList<SlskdTransferDirectory>? Directories);

public sealed record SlskdTransferDirectory(string Directory, IReadOnlyList<SlskdTransfer>? Files);

/// <summary>A file to enqueue: full remote name and the size the peer reported.</summary>
public sealed record SlskdEnqueueFile(string Filename, long Size);

/// <summary>A file slskd refused to queue.</summary>
public sealed record SlskdBatchFailure(string? Filename, string? Message);

/// <summary>
/// HTTP client for the slskd API (<c>/api/v0</c>, <c>X-API-Key</c>) as the Soulseek downloader uses it: queueing and
/// watching transfers. Searching Soulseek is the business of the slskd source extension.
/// </summary>
public sealed class SlskdClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public SlskdClient(HttpClient http, SlskdOptions options)
    {
        _http = http;
        Options = options;
    }

    public SlskdOptions Options { get; }

    /// <summary>
    /// Queues downloads from one user as a batch (slskd 0.26+) stored under
    /// <paramref name="destination"/>, a directory relative to slskd's downloads directory.
    /// Returns the transfers slskd accepted and the files it refused.
    /// </summary>
    public async Task<(IReadOnlyList<SlskdTransfer> Enqueued, IReadOnlyList<SlskdBatchFailure> Failures)> EnqueueBatchAsync(
        string username, IReadOnlyList<SlskdEnqueueFile> files, string destination, CancellationToken cancellationToken)
    {
        var request = new
        {
            id = Guid.NewGuid(),
            username,
            files,
            options = new { destination },
        };

        BatchResponse? response;
        try
        {
            response = await SendAsync<BatchResponse>(HttpMethod.Post, "transfers/downloads/batches", request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SourceException ex) when (ex.Kind == SourceErrorKind.Error && ex.Message.Contains("failed with 400", StringComparison.Ordinal))
        {
            // Before 0.26 the route is taken by the per-user endpoint, which rejects the body.
            throw new SourceException(SourceErrorKind.Error, ex.Message + " Convy needs slskd 0.26 or newer.", ex);
        }

        return (response?.Batch?.Transfers ?? [], response?.Failures ?? []);
    }

    /// <summary>The destination directory of a batch, or <c>null</c> when it has none or is gone.</summary>
    public async Task<string?> GetBatchDestinationAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await SendAsync<SlskdBatch>(
            HttpMethod.Get, $"transfers/downloads/batches/{batchId}", null, cancellationToken, notFoundIsNull: true).ConfigureAwait(false);
        return batch?.Options?.Destination;
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
            throw SourceException.Unreachable("slskd", ex);
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

    private sealed record BatchResponse(SlskdBatch? Batch, List<SlskdBatchFailure>? Failures);

    private sealed record SlskdBatch(Guid Id, List<SlskdTransfer>? Transfers, SlskdBatchOptions? Options);

    private sealed record SlskdBatchOptions(string? Destination);
}
