using Convy.Services.Diagnostics;
using Convy.Services.Jobs;

namespace Convy.Services.Ui;

/// <summary>
/// The log entries the web UI shows, each with the job it is about. Entries written for a job
/// carry its id; those of a downloader only know the item, so the job is looked up by it.
/// </summary>
public sealed class UiLogService
{
    private readonly LogBuffer _buffer;
    private readonly IJobStore _jobs;

    public UiLogService(LogBuffer buffer, IJobStore jobs)
    {
        _buffer = buffer;
        _jobs = jobs;
    }

    /// <summary>
    /// Entries newer than <paramref name="after"/> at <paramref name="minimum"/> severity or above,
    /// containing <paramref name="contains"/> (in the message, logger, exception or job id) and
    /// about <paramref name="job"/> (<c>j_42</c> or <c>42</c>) when given; at most
    /// <paramref name="limit"/>, the newest ones when there are more.
    /// </summary>
    public async Task<LogPage> ReadAsync(
        long after, LogSeverity minimum, string? contains, string? job, int limit, CancellationToken cancellationToken)
    {
        var (entries, last) = _buffer.Snapshot(after);
        var candidates = entries.Where(e => e.Level >= minimum).ToList();

        var items = candidates
            .Where(e => e.JobId is null)
            .Select(ItemRefOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var jobsByItem = items.Count == 0
            ? new Dictionary<string, string>()
            : await _jobs.GetJobIdsByItemAsync(items, cancellationToken).ConfigureAwait(false);

        var wantedJob = NormalizeJobId(job);
        var matching = candidates
            .Select(e => e.JobId is null && ItemRefOf(e) is { } item && jobsByItem.TryGetValue(item, out var id) ? e with { JobId = id } : e)
            .Where(e => wantedJob is null || string.Equals(e.JobId, wantedJob, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrWhiteSpace(contains) || Matches(e, contains.Trim()))
            .ToList();

        var take = Math.Clamp(limit, 1, LogBuffer.DefaultCapacity);
        return new LogPage(matching.Count > take ? matching.GetRange(matching.Count - take, take) : matching, last);
    }

    private static string? ItemRefOf(LogEntry entry) =>
        entry.Properties?.GetValueOrDefault(LogScopes.ItemRef) as string;

    private static string? NormalizeJobId(string? job)
    {
        if (string.IsNullOrWhiteSpace(job))
        {
            return null;
        }

        var trimmed = job.Trim();
        return int.TryParse(trimmed, out var id) ? JobIds.Format(id) : trimmed;
    }

    private static bool Matches(LogEntry entry, string text) =>
        entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
        || entry.Category?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
        || entry.Exception?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
        || entry.JobId?.Equals(text, StringComparison.OrdinalIgnoreCase) == true;
}
