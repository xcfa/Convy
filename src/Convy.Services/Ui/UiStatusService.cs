using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Media;
using Convy.Services.Security;
using Convy.Services.Storage;
using Convy.Services.Sync;
using Microsoft.Extensions.Options;

namespace Convy.Services.Ui;

/// <summary>Everything the overview page shows.</summary>
public sealed record UiStatus(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("now")] DateTimeOffset Now,
    [property: JsonPropertyName("sync")] UiSyncStatus Sync,
    [property: JsonPropertyName("downloaders")] IReadOnlyList<UiDownloaderStatus> Downloaders,
    [property: JsonPropertyName("sources")] IReadOnlyList<SourceDto> Sources,
    [property: JsonPropertyName("storage")] UiStorageStatus Storage,
    [property: JsonPropertyName("jobs")] IReadOnlyDictionary<string, int> Jobs,
    [property: JsonPropertyName("mcp_enabled")] bool McpEnabled);

public sealed record UiSyncStatus(
    [property: JsonPropertyName("auto_sync")] bool AutoSync,
    [property: JsonPropertyName("interval_seconds")] double IntervalSeconds,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("last_started_at")] DateTimeOffset? LastStartedAt,
    [property: JsonPropertyName("last_finished_at")] DateTimeOffset? LastFinishedAt,
    [property: JsonPropertyName("last_error")] string? LastError);

/// <param name="LastSyncAt">When the downloader was last read by a sync cycle; <c>null</c> before the first one.</param>
public sealed record UiDownloaderStatus(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("last_sync_at")] DateTimeOffset? LastSyncAt,
    [property: JsonPropertyName("ok")] bool? Ok,
    [property: JsonPropertyName("items")] int? Items,
    [property: JsonPropertyName("processed")] int? Processed,
    [property: JsonPropertyName("error")] string? Error);

public sealed record UiStorageStatus(
    [property: JsonPropertyName("checked_at")] DateTimeOffset? CheckedAt,
    [property: JsonPropertyName("problems")] IReadOnlyList<string> Problems,
    [property: JsonPropertyName("unchecked")] IReadOnlyList<string> Unchecked);

/// <summary>Collects the state of the service for the UI's overview page.</summary>
public sealed class UiStatusService
{
    private static readonly string AppVersion = ReadVersion();
    private static readonly DateTimeOffset ProcessStartedAt = ReadStartTime();

    private readonly ISyncControlService _sync;
    private readonly SyncStatusTracker _syncStatus;
    private readonly IDownloaderResolver _downloaders;
    private readonly MediaCatalogService _catalog;
    private readonly StorageLayoutStatus _storage;
    private readonly IJobStore _jobs;
    private readonly IOptionsMonitor<McpOptions> _mcp;
    private readonly TimeProvider _timeProvider;

    public UiStatusService(
        ISyncControlService sync,
        SyncStatusTracker syncStatus,
        IDownloaderResolver downloaders,
        MediaCatalogService catalog,
        StorageLayoutStatus storage,
        IJobStore jobs,
        IOptionsMonitor<McpOptions> mcp,
        TimeProvider timeProvider)
    {
        _sync = sync;
        _syncStatus = syncStatus;
        _downloaders = downloaders;
        _catalog = catalog;
        _storage = storage;
        _jobs = jobs;
        _mcp = mcp;
        _timeProvider = timeProvider;
    }

    public async Task<UiStatus> GetAsync(CancellationToken cancellationToken)
    {
        var sync = _sync.GetStatus();
        var cycle = _syncStatus.LastCycle;
        var synced = _syncStatus.Downloaders.ToDictionary(d => d.Provider, StringComparer.Ordinal);

        var downloaders = _downloaders.All
            .Select(d => synced.TryGetValue(d.Provider, out var result)
                ? new UiDownloaderStatus(d.Provider, ProtocolNames.ToName(d.Protocol), result.At, result.Ok, result.Items, result.Processed, result.Error)
                : new UiDownloaderStatus(d.Provider, ProtocolNames.ToName(d.Protocol), null, null, null, null, null))
            .ToList();

        var sources = await _catalog.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        var counts = await _jobs.CountByStatusAsync(cancellationToken).ConfigureAwait(false);

        return new UiStatus(
            AppVersion,
            ProcessStartedAt,
            _timeProvider.GetUtcNow(),
            new UiSyncStatus(sync.AutoSyncEnabled, sync.IntervalSeconds, sync.IsSyncing, cycle?.StartedAt, cycle?.FinishedAt, cycle?.Error),
            downloaders,
            sources.Sources,
            new UiStorageStatus(_storage.CheckedAt, _storage.Problems, _storage.Unchecked),
            Enum.GetValues<JobStatus>().ToDictionary(s => s.ToName(), s => counts.GetValueOrDefault(s)),
            !string.IsNullOrWhiteSpace(_mcp.CurrentValue.ApiKey));
    }

    private static string ReadVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UiStatusService).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";

        // The SDK appends "+<full commit hash>"; a short hash is enough to tell builds apart.
        var plus = version.IndexOf('+');
        return plus < 0 || version.Length - plus <= 8 ? version : version[..(plus + 8)];
    }

    private static DateTimeOffset ReadStartTime()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}
