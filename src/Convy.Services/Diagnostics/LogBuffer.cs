using System.Text.Json.Serialization;

namespace Convy.Services.Diagnostics;

/// <summary>Severity of a log entry, in increasing order.</summary>
public enum LogSeverity
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

/// <summary>One log entry kept for the UI.</summary>
/// <param name="Sequence">Increasing number; lets a client ask for entries after the last one it has.</param>
/// <param name="Timestamp">When the entry was written.</param>
/// <param name="Level">Severity.</param>
/// <param name="Category">Source context (logger name), when known.</param>
/// <param name="Message">Rendered message.</param>
/// <param name="Exception">Exception text, when the entry has one.</param>
/// <param name="JobId">The job the entry is about (<c>j_42</c>), when known.</param>
/// <param name="Properties">
/// The structured properties: those of the message template and of the scopes it was written
/// in (<see cref="LogScopes"/>); <c>null</c> for none.
/// </param>
public sealed record LogEntry(
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("time")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("level")] LogSeverity Level,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("exception")] string? Exception,
    [property: JsonPropertyName("job_id")] string? JobId = null,
    [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, object?>? Properties = null);

/// <summary>A page of log entries.</summary>
/// <param name="Entries">Matching entries, oldest first.</param>
/// <param name="LastSequence">Sequence of the newest entry in the buffer; pass it back as <c>after</c>.</param>
public sealed record LogPage(
    [property: JsonPropertyName("entries")] IReadOnlyList<LogEntry> Entries,
    [property: JsonPropertyName("last_seq")] long LastSequence);

/// <summary>
/// Keeps the most recent log entries in memory for the UI. Writers never block for long:
/// adding is a short critical section; the oldest entries are dropped beyond the capacity.
/// </summary>
public sealed class LogBuffer
{
    /// <summary>Entries kept by default.</summary>
    public const int DefaultCapacity = 5000;

    private readonly object _gate = new();
    private readonly LogEntry?[] _entries;
    private long _lastSequence;

    public LogBuffer(int capacity = DefaultCapacity)
    {
        _entries = new LogEntry?[Math.Max(1, capacity)];
    }

    public void Add(
        DateTimeOffset timestamp,
        LogSeverity level,
        string? category,
        string message,
        string? exception,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var jobId = properties?.GetValueOrDefault(LogScopes.JobId) as string;
        lock (_gate)
        {
            var sequence = ++_lastSequence;
            _entries[sequence % _entries.Length] =
                new LogEntry(sequence, timestamp, level, category, message, exception, jobId, properties is { Count: > 0 } ? properties : null);
        }
    }

    /// <summary>Every entry newer than <paramref name="after"/>, oldest first, and the newest sequence.</summary>
    public (IReadOnlyList<LogEntry> Entries, long LastSequence) Snapshot(long after)
    {
        lock (_gate)
        {
            var last = _lastSequence;
            var first = Math.Max(after + 1, last - _entries.Length + 1);
            var snapshot = new List<LogEntry>((int)Math.Max(0, last - first + 1));
            for (var sequence = first; sequence <= last; sequence++)
            {
                if (_entries[sequence % _entries.Length] is { } entry && entry.Sequence == sequence)
                {
                    snapshot.Add(entry);
                }
            }

            return (snapshot, last);
        }
    }

    /// <summary>
    /// Entries newer than <paramref name="after"/> at <paramref name="minimum"/> severity or above
    /// whose message, category or exception contains <paramref name="contains"/>; at most
    /// <paramref name="limit"/>, the newest ones when there are more.
    /// </summary>
    public LogPage Read(long after, LogSeverity minimum, string? contains, int limit)
    {
        var (snapshot, last) = Snapshot(after);

        var matching = snapshot
            .Where(e => e.Level >= minimum)
            .Where(e => string.IsNullOrWhiteSpace(contains) || Matches(e, contains))
            .ToList();

        var take = Math.Clamp(limit, 1, _entries.Length);
        return new LogPage(matching.Count > take ? matching.GetRange(matching.Count - take, take) : matching, last);
    }

    private static bool Matches(LogEntry entry, string text) =>
        entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
        || entry.Category?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
        || entry.Exception?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
}
