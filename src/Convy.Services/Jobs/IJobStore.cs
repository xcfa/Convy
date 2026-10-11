namespace Convy.Services.Jobs;

/// <summary>
/// Persistence of jobs and their releases. Abstracted so the job logic is testable without a
/// database. Most methods work on releases (<see cref="JobRecord"/>); a job is every release
/// with the same group id.
/// </summary>
public interface IJobStore
{
    /// <summary>Stores a job with a single release and returns the release with its ids.</summary>
    Task<JobRecord> CreateAsync(JobRecord job, CancellationToken cancellationToken);

    /// <summary>
    /// Stores one job made of <paramref name="releases"/> (in this order) and returns them with
    /// their ids; the job's id is the first release's id.
    /// </summary>
    Task<IReadOnlyList<JobRecord>> CreateGroupAsync(IReadOnlyList<JobRecord> releases, CancellationToken cancellationToken);

    /// <summary>One release by its own id.</summary>
    Task<JobRecord?> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>The releases of job <paramref name="groupId"/>, oldest first; empty when there is no such job.</summary>
    Task<IReadOnlyList<JobRecord>> GetGroupAsync(int groupId, CancellationToken cancellationToken);

    /// <summary>The newest release of the item that is not finished, or <c>null</c>.</summary>
    Task<JobRecord?> FindActiveAsync(string provider, string itemRef, CancellationToken cancellationToken);

    /// <summary>The newest <paramref name="limit"/> jobs, newest first, each with its releases.</summary>
    Task<IReadOnlyList<IReadOnlyList<JobRecord>>> ListGroupsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>How many jobs there are in each (combined) status; statuses without jobs are left out.</summary>
    Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Releases that drive placement for the provider's items: every release that is not
    /// cancelled, newest last. Terminal ones are included so re-emitted items keep their job context.
    /// </summary>
    Task<IReadOnlyList<JobRecord>> GetPlacementJobsAsync(string provider, CancellationToken cancellationToken);

    /// <summary>
    /// Saves <paramref name="updated"/> only if the stored status is still
    /// <paramref name="expectedStatus"/>. Returns <c>false</c> when another writer got there first.
    /// </summary>
    Task<bool> TryUpdateAsync(JobRecord updated, JobStatus expectedStatus, CancellationToken cancellationToken);
}
