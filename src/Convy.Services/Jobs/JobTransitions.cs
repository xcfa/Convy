namespace Convy.Services.Jobs;

/// <summary>
/// Saves release updates without overwriting concurrent writers and announces job status
/// changes. Every component that changes a job (sync worker, MCP tools) goes through here.
/// A release's status change is announced only when it changes the status of its job, so a
/// job with several releases produces one event per job status, not one per release.
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
    /// state, and publishes a <see cref="JobStatusChange"/> when the job's status changed.
    /// Returns the saved release, or <c>null</c> when another writer changed it first.
    /// </summary>
    public async Task<JobRecord?> ApplyAsync(JobRecord current, JobRecord updated, CancellationToken cancellationToken)
    {
        if (await SaveCoreAsync(current, updated, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        if (updated.Status != current.Status)
        {
            await PublishReleaseChangeAsync(updated, current.Status, cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>
    /// Persists <paramref name="updated"/> like <see cref="ApplyAsync"/> but without publishing;
    /// the caller publishes once the change is final (<see cref="PublishAsync"/>).
    /// </summary>
    public Task<JobRecord?> SaveAsync(JobRecord current, JobRecord updated, CancellationToken cancellationToken) =>
        SaveCoreAsync(current, updated, cancellationToken);

    /// <summary>Announces a job status change saved with <see cref="SaveAsync"/>.</summary>
    public Task PublishAsync(JobStatusChange change, CancellationToken cancellationToken) =>
        _events.PublishAsync(change, cancellationToken);

    /// <summary>Stores a new job with one release and publishes its initial status.</summary>
    public async Task<JobRecord> CreateAsync(JobRecord job, CancellationToken cancellationToken) =>
        (await CreateGroupAsync([job], cancellationToken).ConfigureAwait(false)).Releases[0];

    /// <summary>Stores a new job made of <paramref name="releases"/> and publishes its initial status once.</summary>
    public async Task<JobState> CreateGroupAsync(IReadOnlyList<JobRecord> releases, CancellationToken cancellationToken)
    {
        var created = JobState.From(await _store.CreateGroupAsync(releases, cancellationToken).ConfigureAwait(false));
        await _events.PublishAsync(new JobStatusChange(created, PreviousStatus: null), cancellationToken).ConfigureAwait(false);
        return created;
    }

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

    private async Task PublishReleaseChangeAsync(JobRecord release, JobStatus previous, CancellationToken cancellationToken)
    {
        var releases = await _store.GetGroupAsync(release.GroupId, cancellationToken).ConfigureAwait(false);
        if (releases.Count == 0)
        {
            releases = [release];
        }

        // The stored group already holds this release's new state; the job's previous status
        // is the same group with this release as it was.
        var after = JobState.From(releases.Select(r => r.Id == release.Id ? release : r).ToList());
        var before = JobState.Combine(after.Releases.Select(r => r.Id == release.Id ? previous : r.Status));

        if (after.Status != before)
        {
            await _events.PublishAsync(new JobStatusChange(after, before), cancellationToken).ConfigureAwait(false);
        }
    }
}
