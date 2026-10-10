namespace Convy.Services.Jobs;

/// <summary>
/// A job as the agent and the user see it: one download request with one or more releases
/// (<see cref="JobRecord"/>s with the same group id). Status, size, path and error are
/// combined from the releases.
/// </summary>
public sealed record JobState
{
    public required int Id { get; init; }

    /// <summary>Public identifier, e.g. <c>j_42</c>.</summary>
    public string JobId => JobIds.Format(Id);

    /// <summary>The releases, oldest first; never empty.</summary>
    public required IReadOnlyList<JobRecord> Releases { get; init; }

    public required JobStatus Status { get; init; }

    public bool HasManyReleases => Releases.Count > 1;

    /// <summary>The first release's title, plus how many others there are.</summary>
    public string Title => HasManyReleases ? $"{Releases[0].Title} (+{Releases.Count - 1} more)" : Releases[0].Title;

    public string Category => Releases[0].Category;

    /// <summary>The downloaders involved, e.g. <c>slskd</c> or <c>qbittorrent, slskd</c>.</summary>
    public string Provider => string.Join(", ", Releases.Select(r => r.Provider).Distinct(StringComparer.Ordinal));

    /// <summary>The rule shared by every release, or <c>null</c> when they differ or none matched.</summary>
    public string? Rule => Releases.Select(r => r.Rule).Distinct(StringComparer.Ordinal).Count() == 1 ? Releases[0].Rule : null;

    /// <summary>The release's directory; for several releases the deepest directory containing all of them.</summary>
    public string? TargetPath => HasManyReleases ? CommonDirectory(Releases.Select(r => r.TargetPath)) : Releases[0].TargetPath;

    public long? SizeBytes => SumOrNull(Releases.Select(r => r.SizeBytes));

    public int? FileCount => Releases.Any(r => r.FileCount is not null) ? Releases.Sum(r => r.FileCount ?? 0) : null;

    /// <summary>The release's error; for several releases each error prefixed with its title.</summary>
    public string? Error => CombineErrors(Releases.Select(r => (r.Title, r.Error)));

    public DateTimeOffset CreatedAt => Releases.Min(r => r.CreatedAt);

    public DateTimeOffset? CompletedAt => Status.IsTerminal() ? Releases.Max(r => r.CompletedAt) : null;

    /// <summary>Placed files relative to <see cref="TargetPath"/>, from every completed release.</summary>
    public IReadOnlyList<string> PlacedFiles
    {
        get
        {
            if (!HasManyReleases)
            {
                return Releases[0].PlacedFiles ?? [];
            }

            var root = TargetPath;
            return Releases
                .Where(r => r.PlacedFiles is not null)
                .SelectMany(r => r.PlacedFiles!.Select(file => RelativeTo(root, r.TargetPath, file)))
                .ToList();
        }
    }

    /// <summary>The job made of <paramref name="releases"/> (all of one group).</summary>
    public static JobState From(IReadOnlyList<JobRecord> releases) =>
        From(releases, releases.Select(r => r.Status).ToList());

    /// <summary>The job made of <paramref name="releases"/> with their statuses taken from <paramref name="statuses"/>.</summary>
    public static JobState From(IReadOnlyList<JobRecord> releases, IReadOnlyList<JobStatus> statuses)
    {
        if (releases.Count == 0)
        {
            throw new ArgumentException("A job has at least one release.", nameof(releases));
        }

        var ordered = releases.Select((r, i) => (Release: r, Status: statuses[i])).OrderBy(x => x.Release.Id).ToList();
        return new JobState
        {
            Id = ordered[0].Release.GroupId == 0 ? ordered[0].Release.Id : ordered[0].Release.GroupId,
            Releases = ordered.Select(x => x.Release).ToList(),
            Status = Combine(ordered.Select(x => x.Status)),
        };
    }

    /// <summary>
    /// The status of a job from its releases' statuses. While any release is active the job is
    /// active: downloading if one downloads, else stalled, queued, placing in that order. Once
    /// all are finished: failed if one failed, else cancelled if one was cancelled, else completed.
    /// </summary>
    public static JobStatus Combine(IEnumerable<JobStatus> statuses)
    {
        var all = statuses.ToList();
        if (all.Count == 0)
        {
            throw new ArgumentException("A job has at least one release.", nameof(statuses));
        }

        if (all.Any(s => !s.IsTerminal()))
        {
            foreach (var active in new[] { JobStatus.Downloading, JobStatus.Stalled, JobStatus.Queued })
            {
                if (all.Contains(active))
                {
                    return active;
                }
            }

            return JobStatus.Placing;
        }

        return all.Contains(JobStatus.Failed) ? JobStatus.Failed
            : all.Contains(JobStatus.Cancelled) ? JobStatus.Cancelled
            : JobStatus.Completed;
    }

    /// <summary>Errors joined as <c>Title: error; Title: error</c> for several releases, or the only one.</summary>
    public static string? CombineErrors(IEnumerable<(string Title, string? Error)> errors)
    {
        var all = errors.ToList();
        if (all.Count == 1)
        {
            return all[0].Error;
        }

        var failed = all.Where(e => !string.IsNullOrEmpty(e.Error)).Select(e => $"{e.Title}: {e.Error}").ToList();
        return failed.Count == 0 ? null : string.Join("; ", failed);
    }

    /// <summary>The deepest directory that contains every path, or <c>null</c>; separated by '/'.</summary>
    public static string? CommonDirectory(IEnumerable<string?> paths)
    {
        var known = paths.Where(p => !string.IsNullOrEmpty(p)).Select(p => Normalize(p!).TrimEnd('/')).ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var common = known[0].Split('/');
        var length = common.Length;
        foreach (var path in known.Skip(1))
        {
            var segments = path.Split('/');
            var shared = 0;
            while (shared < Math.Min(length, segments.Length) && segments[shared] == common[shared])
            {
                shared++;
            }

            length = shared;
        }

        var result = string.Join('/', common.Take(length));
        return result.Length == 0 && known[0].StartsWith('/') ? "/" : result.Length == 0 ? null : result;
    }

    private static string RelativeTo(string? root, string? directory, string file)
    {
        if (root is null || directory is null)
        {
            return file;
        }

        var normalized = Normalize(directory);
        if (!normalized.StartsWith(root, StringComparison.Ordinal))
        {
            return file;
        }

        var prefix = normalized[root.Length..].Trim('/');
        return prefix.Length == 0 ? file : $"{prefix}/{file}";
    }

    /// <summary>Paths use '/' (Linux); on Windows (development) the native separator is accepted too.</summary>
    private static string Normalize(string path) =>
        Path.DirectorySeparatorChar == '/' ? path : path.Replace(Path.DirectorySeparatorChar, '/');

    private static long? SumOrNull(IEnumerable<long?> values)
    {
        var known = values.Where(v => v is not null).ToList();
        return known.Count == 0 ? null : known.Sum(v => v!.Value);
    }
}
