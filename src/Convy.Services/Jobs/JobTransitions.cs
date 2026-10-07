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
        if (await SaveCoreAsync(current, updated, cancellationToken).ConfigureAwait(false) is null)
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

    /// <summary>
    /// Persists <paramref name="updated"/> like <see cref="ApplyAsync"/> but without publishing;
    /// the caller publishes once the change is final (<see cref="PublishAsync"/>).
    /// </summary>
    public Task<JobRecord?> SaveAsync(JobRecord current, JobRecord updated, CancellationToken cancellationToken) =>
        SaveCoreAsync(current, updated, cancellationToken);

    /// <summary>Announces a status change saved with <see cref="SaveAsync"/>.</summary>
    public Task PublishAsync(JobStatusChange change, CancellationToken cancellationToken) =>
        _events.PublishAsync(change, cancellationToken);

    private async Task<JobRecord?> SaveCoreAsync(JobRecord current, JobRecord updated, CancellationToken cancellationToken)
    {
        if (updated == current)
        {
            return current;
        }

        return await _store.TryUpdateAsync(updated, current.Status, cancellationToken).ConfigureAwait(false)
            ? updated
            : null;
    }

    /// <summary>Stores a new job and publishes its initial status.</summary>
    public async Task<JobRecord> CreateAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var created = await _store.CreateAsync(job, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(new JobStatusChange(created, PreviousStatus: null), cancellationToken).ConfigureAwait(false);
        return created;
    }
}
