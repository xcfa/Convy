namespace Convy.Services.Files;

/// <summary>Queries persisted linked file entries with optional filtering.</summary>
public interface IFileEntryQueryService
{
    /// <summary>
    /// Returns linked file entries matching <paramref name="filter"/>.
    /// All filter fields are combined with AND.
    /// </summary>
    Task<List<FileEntryDto>> QueryAsync(FileEntryFilter filter, CancellationToken cancellationToken);
}

/// <summary>Filter for <see cref="IFileEntryQueryService.QueryAsync"/>. Fields combine with AND.</summary>
public class FileEntryFilter
{
    /// <summary>Exact info hash.</summary>
    public string? InfoHash { get; set; }

    /// <summary>Substring match on torrent name.</summary>
    public string? TorrentName { get; set; }

    /// <summary>Substring match on file path or target path.</summary>
    public string? Path { get; set; }

    /// <summary>Exact date (entries linked on that day). Overrides From/To.</summary>
    public DateTimeOffset? Date { get; set; }

    /// <summary>Start of the date range (inclusive).</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>End of the date range (exclusive).</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>Maximum number of entries to return. Default 500.</summary>
    public int Limit { get; set; } = 500;
}

/// <summary>A linked file entry projected for the API.</summary>
public class FileEntryDto
{
    public required string InfoHash { get; set; }
    public required string FilePath { get; set; }
    public required string TargetPath { get; set; }
    public string? TorrentName { get; set; }
    public DateTimeOffset LinkedDate { get; set; }
}
