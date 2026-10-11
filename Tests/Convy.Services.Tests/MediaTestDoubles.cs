using System.Runtime.CompilerServices;
using Convy.Services.Media;
using Convy.Services.Webhooks;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

/// <summary>A scriptable source.</summary>
internal sealed class FakeSource : IContentSource
{
    public FakeSource(string id, string? name = null, Protocol protocol = Protocol.Torrent, SourceStatus status = SourceStatus.Ok)
    {
        Id = id;
        Name = name ?? id;
        Protocol = protocol;
        Status = status;
    }

    public string Id { get; }
    public string Name { get; }
    public Protocol Protocol { get; }
    public SourceStatus Status { get; }
    public string? StatusMessage => null;

    /// <summary>Results per query; missing queries return nothing.</summary>
    public Dictionary<string, List<ContentInfo>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? Failure { get; set; }
    public bool Hang { get; set; }
    public List<string> Queries { get; } = [];
    public FileListing? Listing { get; set; }
    public bool HangOnListing { get; set; }
    public DownloadPayload? Payload { get; set; }
    public int ListingCalls { get; private set; }

    public async IAsyncEnumerable<ContentInfo> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (Queries) Queries.Add(request.Query);

        if (Hang)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        if (Failure is not null)
            throw Failure;

        foreach (var item in Results.GetValueOrDefault(request.Query) ?? [])
            yield return item;
    }

    public async Task<FileListing> ListFilesAsync(string contentId, CancellationToken cancellationToken)
    {
        ListingCalls++;
        if (HangOnListing)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        return Listing ?? throw new SourceException(SourceErrorKind.Error, "no listing");
    }

    /// <summary>Payload per content id, when <see cref="Payload"/> is not set.</summary>
    public Func<string, DownloadPayload>? PayloadFor { get; set; }

    public Task<DownloadPayload> ResolveAsync(string contentId, CancellationToken cancellationToken) =>
        Task.FromResult(Payload ?? PayloadFor?.Invoke(contentId) ?? new TorrentPayload("abc", "magnet:?xt=urn:btih:abc", null));

    public static ContentInfo Content(string title, string? hash = null, int? seeders = null, string? contentId = null) => new()
    {
        ContentId = contentId ?? title,
        Title = title,
        SizeBytes = 1000,
        FileCount = 2,
        DedupKey = hash is null ? null : $"btih:{hash}",
        Availability = new Availability { Seeders = seeders },
    };
}

internal sealed class StaticProvider : ISourceProvider
{
    public StaticProvider(params IContentSource[] sources) => Sources = sources.ToList();

    public List<IContentSource> Sources { get; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }

    public string Name => "static";

    public Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Failure is null
            ? Task.FromResult<IReadOnlyList<IContentSource>>(Sources)
            : Task.FromException<IReadOnlyList<IContentSource>>(Failure);
    }
}

/// <summary>Collects enqueued webhook events.</summary>
internal sealed class RecordingQueue : IWebhookEventQueue
{
    public List<WebhookEvent> Events { get; } = [];

    public void Enqueue(WebhookEvent webhookEvent)
    {
        lock (Events) Events.Add(webhookEvent);
    }
}

internal static class Media
{
    public static SourceHealthMonitor Health(RecordingQueue? queue = null) =>
        new(queue ?? new RecordingQueue(), NullLogger<SourceHealthMonitor>.Instance);

    public static CategoryCatalog Catalog(Dictionary<string, CategoryOptions> categories) =>
        new(new StaticOptions<CategoriesOptions>(new CategoriesOptions { Categories = categories }),
            NullLogger<CategoryCatalog>.Instance);

    public static Dictionary<string, CategoryOptions> Categories(params string[] musicSources) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["music"] = new CategoryOptions
            {
                Description = "Music",
                PathHint = "Artist/Year - Album",
                QbittorrentCategory = "Music",
                Sources = musicSources.Length > 0 ? musicSources.ToList() : null,
            },
            ["other"] = new CategoryOptions { QbittorrentCategory = "Other" },
        };
}
