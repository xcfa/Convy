namespace Convy.Services.Jobs;

/// <summary>
/// Saves job updates without overwriting concurrent writers and announces status changes.
/// Every component that changes a job (sync worker, MCP tools) goes through here.
/// </summary>
public sealed class JobTransitions
{
    private readonly IJobStore _store;
    private readonly IJobEvents _events;

    public JobTransitions(IJobStore store, IJobEvents events)
    {
        _store = store;
        _events = events;
    }

    /// <summary>
    /// Persists <paramref name="updated"/> if <paramref name="current"/> is still the stored
    /// state, and publishes a <see cref="JobStatusChange"/> when the status changed.
    /// Returns the saved job, or <c>null</c> when another writer changed the job first.
    /// </summary>
    public async Task<JobRecord?> ApplyAsync(
        JobRecord current,
        JobRecord updated,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? placedFiles = null)
    {
        if (updated == current)
        {
            return current;
        }

        if (!await _store.TryUpdateAsync(updated, current.Status, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (updated.Status != current.Status)
        {
            await _events.PublishAsync(new JobStatusChange(updated, current.Status, placedFiles), cancellationToken)
                .ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>Stores a new job and publishes its initial status.</summary>
    public async Task<JobRecord> CreateAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var created = await _store.CreateAsync(job, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(new JobStatusChange(created, PreviousStatus: null), cancellationToken).ConfigureAwait(false);
        return created;
    }
}
