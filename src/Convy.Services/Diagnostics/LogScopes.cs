using Convy.Services.Jobs;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Diagnostics;

/// <summary>
/// Log scopes that tie entries to what they are about. Every entry written inside one, by any
/// logger, carries their properties, so the web UI can show the whole story of a job: added,
/// downloaded, placed or not, and why.
/// </summary>
public static class LogScopes
{
    /// <summary>Public job id, e.g. <c>j_42</c>.</summary>
    public const string JobId = "JobId";

    /// <summary>Downloader of the item (<c>qbittorrent</c>, <c>slskd</c>, <c>opds</c>).</summary>
    public const string Provider = "Provider";

    /// <summary>Item reference inside the downloader; the UI finds the job of an item by it.</summary>
    public const string ItemRef = "ItemRef";

    /// <summary>Entries about a downloader item, before or without a job.</summary>
    public static IDisposable? Item(ILogger logger, string provider, string itemRef) =>
        logger.BeginScope(new Dictionary<string, object?> { [Provider] = provider, [ItemRef] = itemRef });

    /// <summary>Entries about one release of a job.</summary>
    public static IDisposable? Job(ILogger logger, JobRecord job) =>
        logger.BeginScope(new Dictionary<string, object?> { [JobId] = job.JobId, [Provider] = job.Provider, [ItemRef] = job.ItemRef });
}
