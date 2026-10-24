using Convy.Sources;

namespace Convy.Services.External;

// Response shapes of the REST API for external clients (the Shizuka provider). Property names are the contract
// (camelCase as ASP.NET Core writes them); null properties are omitted.

/// <summary><c>GET /api/sources</c>: the gallery sources and what they can do.</summary>
public sealed record ApiSourceDto(
    string Id,
    string Name,
    string Status,
    string? Message,
    IReadOnlyList<string> Domains,
    bool Nsfw,
    IReadOnlyList<string> Languages,
    int? DefaultAgeRating,
    bool CanSearch,
    IReadOnlyList<ApiFilterDto> Filters);

public sealed record ApiFilterDto(string Key, string Name, bool Multiple, IReadOnlyList<ApiFilterOptionDto> Options);

public sealed record ApiFilterOptionDto(string Value, string Name);

/// <summary>A release as the API shows it: what the source knows, metadata included.</summary>
public sealed record ApiContentDto(
    string ContentId,
    string Title,
    long? SizeBytes,
    int? FileCount,
    string? InfoUrl,
    IReadOnlyDictionary<string, string> Properties)
{
    public static ApiContentDto From(ContentInfo content) =>
        new(content.ContentId, content.Title, content.SizeBytes, content.FileCount, content.InfoUrl, content.Properties);
}

/// <summary><c>GET /api/sources/{id}/search</c>: one page of releases.</summary>
public sealed record ApiSearchDto(IReadOnlyList<ApiContentDto> Items, string? NextPage);

/// <summary><c>POST /api/jobs</c> body.</summary>
public sealed record ApiStartJobRequest(string SourceId, string ContentId, string? Category = null);

/// <summary><c>GET /api/jobs/{id}</c>: the job's state and the gallery's progress.</summary>
/// <param name="JobId">The job (<c>j_42</c>).</param>
/// <param name="Status">Convy's job status: queued, downloading, stalled, placing, completed, failed, cancelled.</param>
/// <param name="Title">The release's name.</param>
/// <param name="PagesDone">Pages fetched so far.</param>
/// <param name="PagesTotal">Pages of the gallery, once listed.</param>
/// <param name="SizeBytes">The archive's size, once complete.</param>
/// <param name="ArchiveReady">Whether <c>GET /api/jobs/{id}/archive</c> can stream the archive now.</param>
/// <param name="Error">Why the job failed or is retrying, when it is.</param>
public sealed record ApiJobDto(
    string JobId,
    string Status,
    string Title,
    int PagesDone,
    int? PagesTotal,
    long? SizeBytes,
    bool ArchiveReady,
    string? Error);

/// <summary>An error of the API: a message, with the HTTP status the controller maps it to.</summary>
public sealed class ApiException : Exception
{
    public ApiException(int status, string message, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
    }

    public int Status { get; }
}
