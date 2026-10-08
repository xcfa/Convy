using System.Collections.Concurrent;
using Convy.Services.Downloads;
using Convy.Sources;
using Convy.Sources.Slskd;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Downloaders.Slskd;

/// <summary>A transfer together with the item it belongs to and where its file lands.</summary>
/// <param name="Transfer">The slskd transfer.</param>
/// <param name="ItemRef">Reference of the item (see <see cref="SoulseekPaths.ItemRef"/>).</param>
/// <param name="Directory">The user's remote folder the item stands for.</param>
/// <param name="SavePath">Local directory the item's paths are relative to.</param>
/// <param name="ItemPath">Local path of the file relative to <paramref name="SavePath"/>.</param>
public sealed record PlacedTransfer(SlskdTransfer Transfer, string ItemRef, string Directory, string SavePath, string ItemPath);

/// <summary>
/// slskd behind <see cref="IDownloader"/>. slskd transfers single files; an item is all
/// transfers of one user's folder. Convy queues its downloads as batches (slskd 0.26+) with an
/// explicit destination per subfolder, <c>&lt;downloads&gt;/convy/&lt;key&gt;/&lt;folder&gt;/…</c>,
/// so the folder keeps its structure and never clashes with another one of the same name.
/// Downloads started in slskd itself are items per remote folder in slskd's default layout.
/// </summary>
public sealed class SlskdDownloader : IDownloader
{
    private readonly SlskdClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SlskdDownloader> _logger;

    // Batch destinations never change; "" marks a batch without one.
    private readonly ConcurrentDictionary<Guid, string> _batchDestinations = new();

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
            .Select(f => (File: f, Relative: SoulseekPaths.RelativeTo(folder.Directory, f.Filename)))
            .Where(f => selected is null || selected.Contains(f.Relative))
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException("No file of the folder is selected.");
        }

        // One batch per subfolder: a batch has a single destination directory.
        var queued = 0;
        var failures = new List<string>();
        foreach (var subfolder in files.GroupBy(f => SubfolderOf(f.Relative)))
        {
            var destination = SoulseekPaths.DestinationOf(folder.Username, folder.Directory, subfolder.Key);
            var batch = subfolder.Select(f => new SlskdEnqueueFile(f.File.Filename, f.File.Size)).ToList();

            var (enqueued, failed) = await _client.EnqueueBatchAsync(folder.Username, batch, destination, cancellationToken)
                .ConfigureAwait(false);

            queued += enqueued.Count;
            failures.AddRange(failed.Select(f => $"{SoulseekPaths.FileNameOf(f.Filename ?? "?")}: {f.Message}"));
        }

        if (queued == 0)
        {
            throw new InvalidOperationException(
                $"slskd queued none of the {files.Count} file(s) from {folder.Username}" +
                (failures.Count > 0 ? $": {string.Join("; ", failures.Take(5))}" : "."));
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning("slskd queued {Queued} of {Requested} file(s) from {User}: {Failures}",
                queued, files.Count, folder.Username, string.Join("; ", failures.Take(10)));
        }

        return SoulseekPaths.ItemRef(folder.Username, folder.Directory);
    }

    public string GetItemRef(DownloadPayload payload) =>
        payload is SoulseekPayload folder
            ? SoulseekPaths.ItemRef(folder.Username, folder.Directory)
            : throw new ArgumentException($"slskd cannot download a {payload.Protocol} payload.", nameof(payload));

    public async Task CancelAsync(string itemRef, CancellationToken cancellationToken)
    {
        foreach (var placed in await GetItemTransfersAsync(itemRef, cancellationToken).ConfigureAwait(false))
        {
            if (!IsFinished(placed.Transfer.State))
            {
                await _client.CancelDownloadAsync(placed.Transfer.Username, placed.Transfer.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<DownloadItem>> GetItemsAsync(CancellationToken cancellationToken)
    {
        var downloads = await GetDownloadsPathAsync(cancellationToken).ConfigureAwait(false);
        var users = await _client.GetDownloadsAsync(cancellationToken).ConfigureAwait(false);

        var placed = new List<PlacedTransfer>();
        foreach (var transfer in users.SelectMany(Transfers))
        {
            placed.Add(await PlaceAsync(transfer, downloads, cancellationToken).ConfigureAwait(false));
        }

        return placed
            .GroupBy(p => p.ItemRef, StringComparer.Ordinal)
            .Select(g => ToItem(g.ToList()))
            .ToList();
    }

    public async Task<DownloadItem?> GetItemAsync(string itemRef, CancellationToken cancellationToken)
    {
        var transfers = await GetItemTransfersAsync(itemRef, cancellationToken).ConfigureAwait(false);
        return transfers.Count == 0 ? null : ToItem(transfers);
    }

    public async Task<IReadOnlyList<string>> GetDownloadDirectoriesAsync(CancellationToken cancellationToken) =>
        [await GetDownloadsPathAsync(cancellationToken).ConfigureAwait(false)];

    public async Task<string?> GetDownloadDirectoryAsync(AddOptions options, CancellationToken cancellationToken) =>
        await GetDownloadsPathAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Works out which item a transfer belongs to and where slskd stores its file: from the
    /// destination of its batch when Convy queued it, else from slskd's default layout.
    /// </summary>
    public async Task<PlacedTransfer> PlaceAsync(SlskdTransfer transfer, string downloads, CancellationToken cancellationToken)
    {
        if (transfer.BatchId is { } batchId)
        {
            var destination = await GetBatchDestinationAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (SoulseekPaths.TryParseDestination(destination, transfer.Username, transfer.Filename, out var root, out var itemPath))
            {
                return new PlacedTransfer(
                    transfer,
                    SoulseekPaths.ItemRef(transfer.Username, root),
                    root,
                    Path.Combine(downloads, SoulseekPaths.ConvyFolder, SoulseekPaths.FolderKey(transfer.Username, root)),
                    itemPath);
            }
        }

        var directory = SoulseekPaths.DirectoryOf(transfer.Filename);
        return new PlacedTransfer(
            transfer,
            SoulseekPaths.ManualItemRef(transfer.Username, directory),
            directory,
            downloads,
            SoulseekPaths.LocalPathOf(transfer.Filename));
    }

    /// <summary>
    /// The state of an item from its transfers. A failure is final only when slskd does not
    /// retry it any more; then the item fails as a whole, listing the missing files.
    /// </summary>
    public DownloadItem ToItem(IReadOnlyList<PlacedTransfer> placed)
    {
        var first = placed[0];
        var username = first.Transfer.Username;
        var transfers = placed.Select(p => p.Transfer).ToList();

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

        var files = placed
            .Select(p => new DownloadFile(
                p.ItemPath,
                p.Transfer.Size,
                IsSucceeded(p.Transfer.State) ? 1 : p.Transfer.Size > 0 ? Math.Clamp((double)p.Transfer.BytesTransferred / p.Transfer.Size, 0, 0.999) : 0,
                Selected: true))
            .ToList();

        var size = transfers.Sum(t => t.Size);
        var downloaded = transfers.Sum(t => IsSucceeded(t.State) ? t.Size : t.BytesTransferred);
        var name = SoulseekPaths.LocalFolderOf(first.Directory) is { Length: > 0 } folder ? folder : first.Directory;

        return new DownloadItem
        {
            Provider = Provider,
            ItemRef = first.ItemRef,
            Name = name,
            SavePath = first.SavePath,
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
                ["SavePath"] = first.SavePath,
                ["Progress"] = size > 0 ? (double)downloaded / size : 0,
                ["Username"] = username,
            },
        };
    }

    private async Task<IReadOnlyList<PlacedTransfer>> GetItemTransfersAsync(string itemRef, CancellationToken cancellationToken)
    {
        if (!SoulseekPaths.TryParseItemRef(itemRef, out var username, out _, out _))
        {
            return [];
        }

        var downloads = await GetDownloadsPathAsync(cancellationToken).ConfigureAwait(false);
        var user = await _client.GetUserDownloadsAsync(username, cancellationToken).ConfigureAwait(false);

        var placed = new List<PlacedTransfer>();
        foreach (var transfer in Transfers(user))
        {
            var candidate = await PlaceAsync(transfer, downloads, cancellationToken).ConfigureAwait(false);
            if (candidate.ItemRef == itemRef)
            {
                placed.Add(candidate);
            }
        }

        return placed;
    }

    private async Task<string?> GetBatchDestinationAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (!_batchDestinations.TryGetValue(batchId, out var destination))
        {
            destination = await _client.GetBatchDestinationAsync(batchId, cancellationToken).ConfigureAwait(false) ?? string.Empty;
            _batchDestinations[batchId] = destination;
        }

        return destination.Length == 0 ? null : destination;
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

    /// <summary>The subfolder part of a relative path ('/'-separated), empty for the folder itself.</summary>
    private static string SubfolderOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
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
