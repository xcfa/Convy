using Convy.Services.Downloads;
using Convy.Sources;
using Convy.Sources.Slskd;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Downloaders.Slskd;

/// <summary>
/// slskd behind <see cref="IDownloader"/>. slskd transfers single files; an item is all
/// transfers from one user's remote folder (item reference <c>&lt;user&gt;/&lt;directory&gt;</c>),
/// stored by slskd under <c>&lt;downloads&gt;/&lt;last folder&gt;/</c>.
/// </summary>
public sealed class SlskdDownloader : IDownloader
{
    private readonly SlskdClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SlskdDownloader> _logger;

    private string? _downloadsPath;

    public SlskdDownloader(SlskdClient client, TimeProvider timeProvider, ILogger<SlskdDownloader> logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Provider => DownloadProviders.Slskd;

    public Protocol Protocol => Protocol.Soulseek;

    public async Task<string> AddAsync(
        DownloadPayload payload, FileSelection selection, AddOptions options, CancellationToken cancellationToken)
    {
        if (payload is not SoulseekPayload folder)
        {
            throw new ArgumentException($"slskd cannot download a {payload.Protocol} payload.", nameof(payload));
        }

        var selected = selection.Paths is null ? null : new HashSet<string>(selection.Paths, StringComparer.Ordinal);
        var files = folder.Files
            .Where(f => selected is null || selected.Contains(SoulseekPaths.RelativeTo(folder.Directory, f.Filename)))
            .Select(f => new SlskdEnqueueFile(f.Filename, f.Size))
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException("No file of the folder is selected.");
        }

        var enqueued = await _client.EnqueueAsync(folder.Username, files, cancellationToken).ConfigureAwait(false);
        if (enqueued.Count == 0)
        {
            throw new InvalidOperationException($"slskd queued none of the {files.Count} file(s) from {folder.Username}.");
        }

        if (enqueued.Count < files.Count)
        {
            _logger.LogWarning("slskd queued {Queued} of {Requested} file(s) from {User}.", enqueued.Count, files.Count, folder.Username);
        }

        return SoulseekPaths.ItemRef(folder.Username, folder.Directory);
    }

    public async Task CancelAsync(string itemRef, CancellationToken cancellationToken)
    {
        if (!SoulseekPaths.TryParseItemRef(itemRef, out var username, out var directory))
        {
            return;
        }

        var user = await _client.GetUserDownloadsAsync(username, cancellationToken).ConfigureAwait(false);
        foreach (var transfer in Transfers(user).Where(t => SoulseekPaths.DirectoryOf(t.Filename) == directory && !IsFinished(t.State)))
        {
            await _client.CancelDownloadAsync(username, transfer.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DownloadItem>> GetItemsAsync(CancellationToken cancellationToken)
    {
        var savePath = await GetDownloadsPathAsync(cancellationToken).ConfigureAwait(false);
        var users = await _client.GetDownloadsAsync(cancellationToken).ConfigureAwait(false);

        return users
            .SelectMany(Transfers)
            .GroupBy(t => (t.Username, Directory: SoulseekPaths.DirectoryOf(t.Filename)))
            .Select(g => ToItem(g.Key.Username, g.Key.Directory, g.ToList(), savePath))
            .ToList();
    }

    public async Task<DownloadItem?> GetItemAsync(string itemRef, CancellationToken cancellationToken)
    {
        if (!SoulseekPaths.TryParseItemRef(itemRef, out var username, out var directory))
        {
            return null;
        }

        var savePath = await GetDownloadsPathAsync(cancellationToken).ConfigureAwait(false);
        var user = await _client.GetUserDownloadsAsync(username, cancellationToken).ConfigureAwait(false);
        var transfers = Transfers(user).Where(t => SoulseekPaths.DirectoryOf(t.Filename) == directory).ToList();

        return transfers.Count == 0 ? null : ToItem(username, directory, transfers, savePath);
    }

    public async Task<IReadOnlyList<string>> GetDownloadDirectoriesAsync(CancellationToken cancellationToken) =>
        [await GetDownloadsPathAsync(cancellationToken).ConfigureAwait(false)];

    public async Task<string?> GetDownloadDirectoryAsync(AddOptions options, CancellationToken cancellationToken) =>
        await GetDownloadsPathAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The state of a folder from its transfers. A failure is final only when slskd does not
    /// retry it any more; then a folder with failed files fails as a whole, listing them.
    /// </summary>
    public DownloadItem ToItem(string username, string directory, IReadOnlyList<SlskdTransfer> transfers, string savePath)
    {
        var now = _timeProvider.GetUtcNow();
        var failed = transfers.Where(t => IsFailed(t, now)).ToList();
        var succeeded = transfers.Count(t => IsSucceeded(t.State));

        var state = succeeded == transfers.Count ? DownloadState.Completed
            : failed.Count > 0 && succeeded + failed.Count == transfers.Count ? DownloadState.Failed
            : transfers.Any(t => IsActive(t.State)) || succeeded > 0 ? DownloadState.Downloading
            : DownloadState.Queued;

        var error = state == DownloadState.Failed
            ? $"{failed.Count} file(s) were not downloaded: " +
              string.Join("; ", failed.Take(10).Select(t => $"{SoulseekPaths.FileNameOf(t.Filename)} ({Reason(t)})")) +
              (failed.Count > 10 ? "; …" : string.Empty)
            : null;

        var files = transfers
            .Select(t => new DownloadFile(
                SoulseekPaths.LocalPathOf(t.Filename),
                t.Size,
                IsSucceeded(t.State) ? 1 : t.Size > 0 ? Math.Clamp((double)t.BytesTransferred / t.Size, 0, 0.999) : 0,
                Selected: true))
            .ToList();

        var size = transfers.Sum(t => t.Size);
        var downloaded = transfers.Sum(t => IsSucceeded(t.State) ? t.Size : t.BytesTransferred);
        var name = SoulseekPaths.LocalFolderOf(directory) is { Length: > 0 } folder ? folder : directory;

        return new DownloadItem
        {
            Provider = Provider,
            ItemRef = SoulseekPaths.ItemRef(username, directory),
            Name = name,
            SavePath = savePath,
            State = state,
            Size = size,
            Downloaded = downloaded,
            DownloadSpeed = (long)transfers.Where(t => IsActive(t.State)).Sum(t => t.AverageSpeed),
            Error = error,
            Files = files,
            Properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = name,
                ["Size"] = (double)size,
                ["SavePath"] = savePath,
                ["Progress"] = size > 0 ? (double)downloaded / size : 0,
                ["Username"] = username,
            },
        };
    }

    private async Task<string> GetDownloadsPathAsync(CancellationToken cancellationToken)
    {
        if (_downloadsPath is { } cached)
        {
            return cached;
        }

        var path = _client.Options.DownloadsPath
                   ?? await _client.GetDownloadsDirectoryAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("slskd did not report its downloads directory; set SLSKD__DOWNLOADSPATH.");

        _downloadsPath = path;
        return path;
    }

    private static IEnumerable<SlskdTransfer> Transfers(SlskdUserTransfers? user) =>
        user?.Directories?.SelectMany(d => d.Files ?? []) ?? [];

    private static bool Has(string? state, string flag) =>
        state is not null && state.Split(',').Any(s => s.Trim().Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static bool IsFinished(string? state) => Has(state, "Completed");

    private static bool IsSucceeded(string? state) => IsFinished(state) && Has(state, "Succeeded");

    private static bool IsActive(string? state) => Has(state, "InProgress") || Has(state, "Initializing");

    /// <summary>Failed for good: finished without success and no automatic retry pending.</summary>
    private static bool IsFailed(SlskdTransfer transfer, DateTimeOffset now) =>
        IsFinished(transfer.State) && !IsSucceeded(transfer.State)
        && (transfer.NextAttemptAt is null || transfer.NextAttemptAt <= now);

    private static string Reason(SlskdTransfer transfer)
    {
        var flags = transfer.State?.Split(',').Select(s => s.Trim()).Where(s => s != "Completed") ?? [];
        var reason = string.Join(", ", flags);
        return string.IsNullOrEmpty(transfer.Exception) ? reason : $"{reason}: {transfer.Exception}";
    }
}
