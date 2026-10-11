using System.Text.Json.Serialization;

namespace Convy.Services.Media;

// Response shapes of the MCP tools. Property names are part of the agent-facing contract
// (snake_case); null properties are omitted by the MCP serializer.

/// <summary><c>get_categories</c> response.</summary>
public sealed record CategoriesResponse(
    [property: JsonPropertyName("categories")] IReadOnlyList<CategoryDto> Categories);

public sealed record CategoryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("path_hint")] string? PathHint,
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources);

/// <summary><c>get_sources</c> response.</summary>
public sealed record SourcesResponse(
    [property: JsonPropertyName("sources")] IReadOnlyList<SourceDto> Sources);

public sealed record SourceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message);

/// <summary><c>search</c> / <c>search_next</c> response.</summary>
public sealed record SearchResponse(
    [property: JsonPropertyName("search_id")] string SearchId,
    [property: JsonPropertyName("results")] IReadOnlyList<SearchResultDto> Results,
    [property: JsonPropertyName("sources")] IReadOnlyList<SourceSearchStatusDto> Sources,
    [property: JsonPropertyName("shown")] int Shown,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("note")] string? Note);

public sealed record SearchResultDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("file_count")] int? FileCount,
    [property: JsonPropertyName("availability")] AvailabilityDto Availability,
    [property: JsonPropertyName("matched_queries")] IReadOnlyList<string> MatchedQueries);

public sealed record AvailabilityDto(
    [property: JsonPropertyName("seeders")] int? Seeders,
    [property: JsonPropertyName("leechers")] int? Leechers,
    [property: JsonPropertyName("free_slot")] bool? FreeSlot,
    [property: JsonPropertyName("queue_length")] int? QueueLength,
    [property: JsonPropertyName("speed_bytes_per_sec")] long? SpeedBytesPerSecond);

/// <summary>Outcome of one source in a search step.</summary>
public sealed record SourceSearchStatusDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("results")] int Results,
    [property: JsonPropertyName("message")] string? Message);

/// <summary><c>list_files</c> response.</summary>
public sealed record ListFilesResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result_id")] string ResultId,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("entries")] IReadOnlyList<FileEntryDto>? Entries,
    [property: JsonPropertyName("shown")] int? Shown,
    [property: JsonPropertyName("total")] int? Total,
    [property: JsonPropertyName("offset")] int? Offset,
    [property: JsonPropertyName("has_more")] bool? HasMore,
    [property: JsonPropertyName("note")] string? Note)
{
    public static ListFilesResponse Timeout(string resultId) =>
        new("timeout", resultId, null, null, null, null, null, null,
            "The file list was not received in time. Pick another result, or download this one without include/exclude.");
}

/// <summary>A directory (path ends with '/') with its summary, or a file.</summary>
public sealed record FileEntryDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("files")] int? Files = null,
    [property: JsonPropertyName("types")] IReadOnlyDictionary<string, int>? Types = null);

/// <summary><c>download</c> response.</summary>
public sealed record DownloadResponse(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expected_path")] string? ExpectedPath,
    [property: JsonPropertyName("rule")] string? Rule,
    [property: JsonPropertyName("file_count")] int? FileCount,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("releases")] IReadOnlyList<DownloadReleaseDto>? Releases = null,
    [property: JsonPropertyName("failed")] IReadOnlyList<FailedReleaseDto>? Failed = null);

/// <summary>A release of a <c>download</c> request with several releases.</summary>
public sealed record DownloadReleaseDto(
    [property: JsonPropertyName("result_id")] string? ResultId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("expected_path")] string? ExpectedPath,
    [property: JsonPropertyName("rule")] string? Rule,
    [property: JsonPropertyName("file_count")] int? FileCount,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes);

/// <summary>A release its downloader did not accept; the job was started without it.</summary>
public sealed record FailedReleaseDto(
    [property: JsonPropertyName("result_id")] string? ResultId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("error")] string Error);

/// <summary><c>get_jobs</c> response.</summary>
public sealed record JobsResponse(
    [property: JsonPropertyName("jobs")] IReadOnlyList<JobDto> Jobs);

public sealed record JobDto(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("progress")] double? Progress,
    [property: JsonPropertyName("downloaded_bytes")] long? DownloadedBytes,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("speed_bytes_per_sec")] long? SpeedBytesPerSecond,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("rule")] string? Rule,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("releases")] IReadOnlyList<JobReleaseDto>? Releases = null);

/// <summary>One release of a job with several releases.</summary>
public sealed record JobReleaseDto(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("progress")] double? Progress,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("rule")] string? Rule,
    [property: JsonPropertyName("error")] string? Error);

/// <summary><c>cancel_job</c> response.</summary>
public sealed record CancelJobResponse(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("note")] string Note);
