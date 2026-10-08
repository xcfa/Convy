namespace Convy.Services.Jobs;

/// <summary>Persistence of jobs. Abstracted so the job logic is testable without a database.</summary>
public interface IJobStore
{
    /// <summary>Stores a new job and returns it with its id.</summary>
    Task<JobRecord> CreateAsync(JobRecord job, CancellationToken cancellationToken);

    Task<JobRecord?> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>The newest job of the item that is not finished, or <c>null</c>.</summary>
    Task<JobRecord?> FindActiveAsync(string provider, string itemRef, CancellationToken cancellationToken);

    /// <summary>Newest jobs first, optionally filtered by status.</summary>
    Task<IReadOnlyList<JobRecord>> ListAsync(JobStatus? status, int limit, CancellationToken cancellationToken);

    /// <summary>How many jobs there are in each status; statuses without jobs are left out.</summary>
    Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Jobs that drive placement for the provider's items: every job that is not cancelled,
    /// newest last. Terminal jobs are included so re-emitted items keep their job context.
    /// </summary>
    Task<IReadOnlyList<JobRecord>> GetPlacementJobsAsync(string provider, CancellationToken cancellationToken);

    /// <summary>
    /// Saves <paramref name="updated"/> only if the stored status is still
    /// <paramref name="expectedStatus"/>. Returns <c>false</c> when another writer got there first.
    /// </summary>
    Task<bool> TryUpdateAsync(JobRecord updated, JobStatus expectedStatus, CancellationToken cancellationToken);
}
