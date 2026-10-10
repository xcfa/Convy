using System.Text.Json;
using Convy.Data.Context;
using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Services.Jobs;

/// <summary>EF Core implementation of <see cref="IJobStore"/>.</summary>
public sealed class EfJobStore : IJobStore
{
    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;

    public EfJobStore(IDbContextFactory<ConvyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<JobRecord> CreateAsync(JobRecord job, CancellationToken cancellationToken) =>
        (await CreateGroupAsync([job], cancellationToken).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<JobRecord>> CreateGroupAsync(IReadOnlyList<JobRecord> releases, CancellationToken cancellationToken)
    {
        if (releases.Count == 0)
        {
            throw new ArgumentException("A job has at least one release.", nameof(releases));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The job's id is its first release's id, known only once that one is stored.
        var entries = releases.Select(NewEntry).ToList();
        db.Jobs.Add(entries[0]);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            entry.GroupId = entries[0].Id;
        }

        db.Jobs.AddRange(entries.Skip(1));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return entries.Select(ToRecord).ToList();
    }

    public async Task<JobRecord?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var entry = await db.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return entry is null ? null : ToRecord(entry);
    }

    public async Task<IReadOnlyList<JobRecord>> GetGroupAsync(int groupId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var entries = await db.Jobs.AsNoTracking()
            .Where(j => j.GroupId == groupId)
            .OrderBy(j => j.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entries.Select(ToRecord).ToList();
    }

    public async Task<JobRecord?> FindActiveAsync(string provider, string itemRef, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var finished = Enum.GetValues<JobStatus>().Where(s => s.IsTerminal()).Select(s => s.ToName()).ToList();
        var entry = await db.Jobs.AsNoTracking()
            .Where(j => j.Provider == provider && j.ItemRef == itemRef && !finished.Contains(j.Status))
            .OrderByDescending(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return entry is null ? null : ToRecord(entry);
    }

    public async Task<IReadOnlyList<IReadOnlyList<JobRecord>>> ListGroupsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var groupIds = await db.Jobs.AsNoTracking()
            .Select(j => j.GroupId)
            .Distinct()
            .OrderByDescending(id => id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var entries = await db.Jobs.AsNoTracking()
            .Where(j => groupIds.Contains(j.GroupId))
            .OrderBy(j => j.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byGroup = entries.Select(ToRecord).ToLookup(r => r.GroupId);
        return groupIds.Select(id => (IReadOnlyList<JobRecord>)byGroup[id].ToList()).ToList();
    }

    public async Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // A job's status is combined from its releases, so count per job in memory; the
        // table holds one small row per release.
        var releases = await db.Jobs.AsNoTracking()
            .Select(j => new { j.GroupId, j.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<JobStatus, int>();
        foreach (var job in releases.GroupBy(r => r.GroupId))
        {
            var statuses = job
                .Select(r => JobStatusNames.TryParse(r.Status, out var status) ? (JobStatus?)status : null)
                .Where(s => s is not null)
                .Select(s => s!.Value)
                .ToList();

            if (statuses.Count > 0)
            {
                var combined = JobState.Combine(statuses);
                result[combined] = result.GetValueOrDefault(combined) + 1;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<JobRecord>> GetPlacementJobsAsync(string provider, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var cancelled = JobStatus.Cancelled.ToName();
        var entries = await db.Jobs.AsNoTracking()
            .Where(j => j.Provider == provider && j.Status != cancelled)
            .OrderBy(j => j.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entries.Select(ToRecord).ToList();
    }

    public async Task<bool> TryUpdateAsync(JobRecord updated, JobStatus expectedStatus, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var entry = await db.Jobs
            .FirstOrDefaultAsync(j => j.Id == updated.Id, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null || entry.Status != expectedStatus.ToName())
        {
            return false;
        }

        entry.Status = updated.Status.ToName();
        Apply(updated, entry);

        try
        {
            // Status is a concurrency token: the UPDATE only applies if nobody changed it
            // between the read above and now.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static void Apply(JobRecord job, JobEntry entry)
    {
        entry.ItemRef = job.ItemRef;
        entry.ClientCategory = job.ClientCategory;
        entry.Subpath = job.Subpath;
        entry.SelectedFilesJson = job.SelectedFiles is null ? null : JsonSerializer.Serialize(job.SelectedFiles);
        entry.Title = job.Title;
        entry.SizeBytes = job.SizeBytes;
        entry.FileCount = job.FileCount;
        entry.ResultId = job.ResultId;
        entry.SourceId = job.SourceId;
        entry.Rule = job.Rule;
        entry.TargetPath = job.TargetPath;
        entry.Error = job.Error;
        entry.PlacedFilesJson = job.PlacedFiles is null ? null : JsonSerializer.Serialize(job.PlacedFiles);
        entry.PlacementAttempts = job.PlacementAttempts;
        entry.LastDownloadedBytes = job.LastDownloadedBytes;
        entry.LastProgressAt = job.LastProgressAt;
        entry.CreatedAt = job.CreatedAt;
        entry.UpdatedAt = job.UpdatedAt;
        entry.CompletedAt = job.CompletedAt;
    }

    private static JobEntry NewEntry(JobRecord job)
    {
        var entry = new JobEntry
        {
            Provider = job.Provider,
            ItemRef = job.ItemRef,
            Category = job.Category,
            Title = job.Title,
            Status = job.Status.ToName(),
        };
        Apply(job, entry);
        return entry;
    }

    private static JobRecord ToRecord(JobEntry entry) => new()
    {
        Id = entry.Id,
        GroupId = entry.GroupId,
        Provider = entry.Provider,
        ItemRef = entry.ItemRef,
        Category = entry.Category,
        ClientCategory = entry.ClientCategory,
        Subpath = entry.Subpath,
        SelectedFiles = entry.SelectedFilesJson is null
            ? null
            : JsonSerializer.Deserialize<List<string>>(entry.SelectedFilesJson),
        Title = entry.Title,
        SizeBytes = entry.SizeBytes,
        FileCount = entry.FileCount,
        ResultId = entry.ResultId,
        SourceId = entry.SourceId,
        Status = JobStatusNames.Parse(entry.Status),
        Rule = entry.Rule,
        TargetPath = entry.TargetPath,
        Error = entry.Error,
        PlacedFiles = entry.PlacedFilesJson is null
            ? null
            : JsonSerializer.Deserialize<List<string>>(entry.PlacedFilesJson),
        PlacementAttempts = entry.PlacementAttempts,
        LastDownloadedBytes = entry.LastDownloadedBytes,
        LastProgressAt = entry.LastProgressAt,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
        CompletedAt = entry.CompletedAt,
    };
}
