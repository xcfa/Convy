using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Convy.Sources.Slskd;

/// <summary>Offers Soulseek (through slskd) as a single source, id <c>soulseek</c>.</summary>
public sealed class SoulseekSourceProvider : ISourceProvider
{
    private readonly SlskdClient _client;
    private readonly ILogger<SoulseekSource> _logger;

    public SoulseekSourceProvider(SlskdClient client, ILogger<SoulseekSource> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => "slskd";

    public async Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        if (!_client.Options.IsConfigured)
        {
            return [];
        }

        var (status, message) = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        return [new SoulseekSource(_client, status, message, _logger)];
    }

    private async Task<(SourceStatus, string?)> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client.IsLoggedInAsync(cancellationToken).ConfigureAwait(false)
                ? (SourceStatus.Ok, null)
                : (SourceStatus.Error, "slskd is not logged in to the Soulseek network.");
        }
        catch (SourceException ex)
        {
            return (SourceStatus.Error, ex.Message);
        }
    }
}

/// <summary>
/// Soulseek as a source. A result is one user's folder: search hits are grouped by user and
/// remote directory and filtered by the category's extensions.
/// </summary>
public sealed class SoulseekSource : IContentSource
{
    public const string SourceId = "soulseek";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly SlskdClient _client;
    private readonly ILogger<SoulseekSource> _logger;

    public SoulseekSource(SlskdClient client, SourceStatus status, string? statusMessage, ILogger<SoulseekSource> logger)
    {
        _client = client;
        Status = status;
        StatusMessage = statusMessage;
        _logger = logger;
    }

    public string Id => SourceId;

    public string Name => "Soulseek";

    public Protocol Protocol => Protocol.Soulseek;

    public SourceStatus Status { get; }

    public string? StatusMessage { get; }

    public async IAsyncEnumerable<ContentInfo> SearchAsync(
        SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responses = await RunSearchAsync(request.Query, cancellationToken).ConfigureAwait(false);
        var extensions = request.Category.SoulseekExtensions;

        var folders = responses
            .SelectMany(r => (r.Files ?? []).Select(f => (Response: r, File: f)))
            .Where(x => extensions.Count == 0 || extensions.Contains(ExtensionOf(x.File.Filename)))
            .GroupBy(x => (x.Response.Username, Directory: SoulseekPaths.DirectoryOf(x.File.Filename)));

        foreach (var folder in folders)
        {
            var response = folder.First().Response;
            var files = folder.Select(x => new SlskdEnqueueFile(x.File.Filename, x.File.Size)).ToList();
            var types = string.Join("/", files.Select(f => ExtensionOf(f.Filename)).Where(e => e.Length > 0).Distinct().Take(3));

            yield return new ContentInfo
            {
                ContentId = SoulseekContentRef.Serialize(new SoulseekContentRef(folder.Key.Username, folder.Key.Directory, files)),
                Title = $"{DisplayName(folder.Key.Directory)} [{types}] — {folder.Key.Username}",
                SizeBytes = files.Sum(f => f.Size),
                FileCount = files.Count,
                DedupKey = $"slsk:{folder.Key.Username}\n{folder.Key.Directory}",
                Availability = new Availability
                {
                    FreeUploadSlot = response.HasFreeUploadSlot,
                    QueueLength = response.QueueLength,
                    UploadSpeed = response.UploadSpeed,
                },
            };
        }
    }

    public async Task<FileListing> ListFilesAsync(string contentId, CancellationToken cancellationToken)
    {
        var reference = SoulseekContentRef.Deserialize(contentId);
        var files = await GetFolderFilesAsync(reference, cancellationToken).ConfigureAwait(false);

        return new FileListing(files
            .Select(f => new ListedFile(SoulseekPaths.RelativeTo(reference.Directory, f.Filename), f.Size))
            .ToList());
    }

    public async Task<DownloadPayload> ResolveAsync(string contentId, CancellationToken cancellationToken)
    {
        var reference = SoulseekContentRef.Deserialize(contentId);
        var files = await GetFolderFilesAsync(reference, cancellationToken).ConfigureAwait(false);

        return new SoulseekPayload(
            reference.Username,
            reference.Directory,
            files.Select(f => new SoulseekFile(f.Filename, f.Size)).ToList());
    }

    /// <summary>
    /// Every file of the user's folder as the peer shares it now. When the peer cannot be
    /// browsed (offline, browsing disabled), the files found by the search are used.
    /// </summary>
    private async Task<IReadOnlyList<SlskdEnqueueFile>> GetFolderFilesAsync(SoulseekContentRef reference, CancellationToken cancellationToken)
    {
        try
        {
            var directories = await _client.BrowseDirectoryAsync(reference.Username, reference.Directory, cancellationToken)
                .ConfigureAwait(false);

            var files = directories
                .Where(d => d.Name == reference.Directory || d.Name.StartsWith(reference.Directory + "\\", StringComparison.Ordinal))
                .SelectMany(d => (d.Files ?? []).Select(f => new SlskdEnqueueFile(d.Name + "\\" + f.Filename, f.Size)))
                .ToList();

            if (files.Count > 0)
            {
                return files;
            }
        }
        catch (SourceException ex) when (ex.Kind == SourceErrorKind.Error)
        {
            _logger.LogInformation("Could not browse {User}'s folder ({Message}); using the files found by the search.",
                reference.Username, ex.Message);
        }

        return reference.Files;
    }

    /// <summary>
    /// Runs one slskd search: started, polled until slskd ends it (after a pause without
    /// responses) or stopped after <see cref="SlskdOptions.SearchMaxSeconds"/>, then read.
    /// The search is deleted from slskd afterwards.
    /// </summary>
    private async Task<IReadOnlyList<SlskdResponse>> RunSearchAsync(string query, CancellationToken cancellationToken)
    {
        var search = await _client.StartSearchAsync(query, cancellationToken).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        var maxDuration = TimeSpan.FromSeconds(_client.Options.SearchMaxSeconds);

        try
        {
            var stopped = false;
            while (!search.IsComplete)
            {
                if (!stopped && DateTimeOffset.UtcNow - started >= maxDuration)
                {
                    await _client.StopSearchAsync(search.Id, cancellationToken).ConfigureAwait(false);
                    stopped = true;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                search = await _client.GetSearchAsync(search.Id, cancellationToken).ConfigureAwait(false);
            }

            if (search.State?.Contains("Errored", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new SourceException(SourceErrorKind.Error, $"The Soulseek search failed ({search.State}).");
            }

            return await _client.GetSearchResponsesAsync(search.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DeleteSearchAsync(search.Id).ConfigureAwait(false);
        }
    }

    /// <summary>Cleans up the search; runs even when the caller gave up, but briefly.</summary>
    private async Task DeleteSearchAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _client.DeleteSearchAsync(id, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SourceException or OperationCanceledException)
        {
            _logger.LogDebug("Could not delete slskd search {Id}: {Message}", id, ex.Message);
        }
    }

    private static string ExtensionOf(string filename)
    {
        var name = SoulseekPaths.FileNameOf(filename);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>The last two folders of a remote directory, e.g. <c>Evanescence / 2003 - Fallen</c>.</summary>
    private static string DisplayName(string directory)
    {
        var parts = directory.Split('\\', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("@@")).ToList();
        return parts.Count == 0 ? directory : string.Join(" / ", parts.TakeLast(2));
    }
}

/// <summary>What Convy keeps about a Soulseek folder to list and download it later.</summary>
public sealed record SoulseekContentRef(string Username, string Directory, IReadOnlyList<SlskdEnqueueFile> Files)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(SoulseekContentRef reference) => JsonSerializer.Serialize(reference, Json);

    public static SoulseekContentRef Deserialize(string contentId) =>
        JsonSerializer.Deserialize<SoulseekContentRef>(contentId, Json)
        ?? throw new SourceException(SourceErrorKind.Error, "Corrupted content reference.");
}
