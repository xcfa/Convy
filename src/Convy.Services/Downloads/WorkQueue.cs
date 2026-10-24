using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Convy.Services.Downloads;

/// <summary>
/// Hands items from a downloader to its background worker, lets the downloader stop a running one and read its
/// progress. One per downloader that Convy runs itself (OPDS books, galleries).
/// </summary>
public abstract class WorkQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _progress = new(StringComparer.Ordinal);

    /// <summary>Asks the worker to work on an item (again).</summary>
    public void Enqueue(string itemRef) => _channel.Writer.TryWrite(itemRef);

    /// <summary>The items to work on, in order; for the worker.</summary>
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>The worker started an item; <paramref name="stop"/> stops it. The worker keeps owning it.</summary>
    public void Started(string itemRef, CancellationTokenSource stop) => _running[itemRef] = stop;

    /// <summary>The worker is done with an item (before it disposes its token source).</summary>
    public void Finished(string itemRef)
    {
        _running.TryRemove(itemRef, out _);
        _progress.TryRemove(itemRef, out _);
    }

    /// <summary>Stops the item if it is running right now.</summary>
    public void Stop(string itemRef)
    {
        if (_running.TryGetValue(itemRef, out var stop))
        {
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It just finished.
            }
        }
    }

    /// <summary>Bytes of the part being downloaded right now (the current file, the current page).</summary>
    public void ReportProgress(string itemRef, long bytes) => _progress[itemRef] = bytes;

    public long LiveBytes(string itemRef) => _progress.GetValueOrDefault(itemRef);
}
