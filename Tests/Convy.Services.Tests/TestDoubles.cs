using Convy.Data.Context;
using Convy.Infrastructure.Helpers;
using Convy.PathExpressions.Mappings;
using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Rules;
using Convy.Services.Webhooks;
using Convy.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Convy.Services.Tests;

/// <summary>An in-memory SQLite database that lives as long as the instance.</summary>
internal sealed class TestDb : IDbContextFactory<ConvyDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ConvyDbContext> _options;

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ConvyDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public ConvyDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}

/// <summary>A controllable clock.</summary>
internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Rules parsed from YAML; replace <see cref="Yaml"/> to simulate a reload.</summary>
internal sealed class FakeRules : IRulesProvider
{
    private RulesSnapshot _snapshot = new(ConvyMappings.Empty, 0);

    public string Yaml
    {
        set => _snapshot = new RulesSnapshot(ConvyMappings.ParseYaml(value), _snapshot.Version + 1);
    }

    public RulesSnapshot GetCurrent() => _snapshot;
}

internal sealed class FakeFileSystem : IFileSystemInspector, IFileLinker
{
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Mounts { get; } = new(StringComparer.Ordinal);
    public List<(string Source, string Destination)> Links { get; } = [];
    public HashSet<string> FailingSources { get; } = new(StringComparer.Ordinal);
    public long? FreeSpace { get; set; }

    /// <summary>Symbolic links: path prefix -> real path.</summary>
    public Dictionary<string, string> Symlinks { get; } = new(StringComparer.Ordinal);

    public void AddFile(string path) => Files.Add(Norm(path));

    public string GetRealPath(string path)
    {
        var full = Norm(path);
        foreach (var (link, target) in Symlinks)
        {
            if (full == link || full.StartsWith(link + "/", StringComparison.Ordinal))
            {
                return target + full[link.Length..];
            }
        }

        return full;
    }

    public Task<string?> GetMountIdAsync(string path, CancellationToken cancellationToken)
    {
        var full = Norm(path);
        var best = Mounts.Keys
            .Where(m => full == m || full.StartsWith(m.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(m => m.Length)
            .FirstOrDefault();
        return Task.FromResult(best is null ? null : Mounts[best]);
    }

    public long? GetAvailableFreeSpace(string path) => FreeSpace;

    public bool DirectoryExists(string path) => Directories.Contains(Norm(path));

    public bool Exists(string path) => Files.Contains(Norm(path));

    public void Link(string source, string destination)
    {
        if (FailingSources.Contains(Norm(source)))
            throw new IOException("link() failed with errno 18 (EXDEV)");

        Links.Add((Norm(source), Norm(destination)));
        Files.Add(Norm(destination));
    }

    /// <summary>Normalises Windows test-run separators so expectations can use '/'.</summary>
    public static string Norm(string path) => path.Replace('\\', '/');
}

internal sealed class FakeDownloader : IDownloader
{
    public FakeDownloader(string provider = DownloadProviders.QBittorrent, Protocol protocol = Protocol.Torrent)
    {
        Provider = provider;
        Protocol = protocol;
    }

    public string Provider { get; }
    public Protocol Protocol { get; }

    public Dictionary<string, DownloadItem> Items { get; } = new(StringComparer.Ordinal);
    public List<(DownloadPayload Payload, FileSelection Selection, AddOptions Options)> Added { get; } = [];
    public List<string> Cancelled { get; } = [];
    public string DownloadDirectory { get; set; } = "/data/downloads";
    public Func<DownloadPayload, DownloadItem>? OnAdd { get; set; }
    public bool Unreachable { get; set; }

    public Task<string> AddAsync(DownloadPayload payload, FileSelection selection, AddOptions options, CancellationToken cancellationToken)
    {
        Added.Add((payload, selection, options));
        var item = OnAdd?.Invoke(payload) ?? throw new InvalidOperationException("No OnAdd configured.");
        Items[item.ItemRef] = item;
        return Task.FromResult(item.ItemRef);
    }

    public string GetItemRef(DownloadPayload payload) => payload switch
    {
        TorrentPayload torrent => torrent.InfoHash,
        SoulseekPayload folder => $"{folder.Username}/{folder.Directory}",
        _ => throw new ArgumentException("Unknown payload."),
    };

    public Exception? CancelFailure { get; set; }

    public Task CancelAsync(string itemRef, CancellationToken cancellationToken)
    {
        if (CancelFailure is not null)
            return Task.FromException(CancelFailure);

        Cancelled.Add(itemRef);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DownloadItem>> GetItemsAsync(CancellationToken cancellationToken) =>
        Unreachable
            ? Task.FromException<IReadOnlyList<DownloadItem>>(new HttpRequestException("down"))
            : Task.FromResult<IReadOnlyList<DownloadItem>>(Items.Values.Select(i => i with { Files = [] }).ToList());

    public Task<DownloadItem?> GetItemAsync(string itemRef, CancellationToken cancellationToken) =>
        Unreachable
            ? Task.FromException<DownloadItem?>(new HttpRequestException("down"))
            : Task.FromResult(Items.GetValueOrDefault(itemRef));

    public Task<IReadOnlyList<string>> GetDownloadDirectoriesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([DownloadDirectory]);

    public Task<string?> GetDownloadDirectoryAsync(AddOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(DownloadDirectory);

    public static DownloadItem Item(
        string itemRef,
        DownloadState state,
        string savePath = "/data/downloads",
        long size = 100,
        long downloaded = 0,
        string? category = null,
        params DownloadFile[] files) => new()
    {
        Provider = DownloadProviders.QBittorrent,
        ItemRef = itemRef,
        Name = itemRef,
        SavePath = savePath,
        State = state,
        Size = size,
        Downloaded = downloaded,
        Files = files,
        Properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Category"] = category,
            ["Name"] = itemRef,
            ["Size"] = (double)size,
            ["Tags"] = null,
        },
    };

    public static DownloadFile Done(string path, long size = 10) => new(path, size, 1, Selected: true);
}

internal sealed class RecordingJobEvents : IJobEvents
{
    public List<JobStatusChange> Changes { get; } = [];

    public Task PublishAsync(JobStatusChange change, CancellationToken cancellationToken)
    {
        Changes.Add(change);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingWebhooks : IWebhookNotifier
{
    public List<WebhookBatch> Batches { get; } = [];

    public Task NotifyAsync(WebhookBatch batch, CancellationToken cancellationToken)
    {
        Batches.Add(batch);
        return Task.CompletedTask;
    }
}

internal sealed class StaticOptions<T> : IOptionsMonitor<T>
{
    public StaticOptions(T value) => CurrentValue = value;

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
