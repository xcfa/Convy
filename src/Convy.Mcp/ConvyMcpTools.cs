using System.ComponentModel;
using Convy.Services.Media;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Convy.Mcp;

/// <summary>
/// The MCP tools of Convy. A thin layer: every tool delegates to one service call and returns
/// its response as compact JSON. Parameter names are snake_case because they are the
/// agent-facing contract.
/// </summary>
[McpServerToolType]
public sealed class ConvyMcpTools
{
    private readonly MediaCatalogService _catalog;
    private readonly SearchService _search;
    private readonly FileListingService _files;
    private readonly MediaDownloadService _downloads;
    private readonly ToolRunner _run;

    public ConvyMcpTools(
        MediaCatalogService catalog,
        SearchService search,
        FileListingService files,
        MediaDownloadService downloads,
        ToolRunner run)
    {
        _catalog = catalog;
        _search = search;
        _files = files;
        _downloads = downloads;
        _run = run;
    }

    [McpServerTool(Name = "get_categories", ReadOnly = true)]
    [Description("Lists download categories with their description, path_hint and sources in priority order. " +
                 "Pick the category matching the request; build subpath following its path_hint.")]
    public Task<CallToolResult> GetCategories(CancellationToken cancellationToken) =>
        _run.Run("get_categories", () => _catalog.GetCategoriesAsync(cancellationToken));

    [McpServerTool(Name = "get_sources", ReadOnly = true)]
    [Description("Lists search sources (trackers via Prowlarr, Soulseek) with id, name, protocol and status (ok, error, disabled).")]
    public Task<CallToolResult> GetSources(CancellationToken cancellationToken) =>
        _run.Run("get_sources", () => _catalog.GetSourcesAsync(cancellationToken));

    [McpServerTool(Name = "search", ReadOnly = true, OpenWorld = true)]
    [Description("Searches the first batch of the category's sources with every title variant. Returns search_id, results, " +
                 "a status per searched source (ok, empty, timeout, auth_failed, error) and has_more. If nothing fits and " +
                 "has_more is true, call search_next. File lists are not included: use list_files.")]
    public Task<CallToolResult> Search(
        [Description("Category id from get_categories.")] string category,
        [Description("Title variants: different spellings, with/without dashes, the original title. Extra variants are dropped.")] string[] queries,
        [Description("Optional source ids to search instead of the category's list, in priority order.")] string[]? sources = null,
        CancellationToken cancellationToken = default) =>
        _run.Run("search", () => _search.StartAsync(category, queries, sources, cancellationToken));

    [McpServerTool(Name = "search_next", ReadOnly = true, OpenWorld = true)]
    [Description("Searches the next batch of sources of an earlier search. Results already shown are not repeated.")]
    public Task<CallToolResult> SearchNext(
        [Description("search_id returned by search.")] string search_id,
        CancellationToken cancellationToken = default) =>
        _run.Run("search_next", () => _search.NextAsync(search_id, cancellationToken));

    [McpServerTool(Name = "list_files", ReadOnly = true, OpenWorld = true)]
    [Description("Lists the files of a result without downloading it. Without path: the top level, each directory summarised " +
                 "(file count, size, extensions). path expands one directory by one level; glob returns matching files as a " +
                 "flat list. Paged with offset. status 'timeout' means the list could not be obtained: pick another result " +
                 "or download this one whole.")]
    public Task<CallToolResult> ListFiles(
        [Description("Result id from search.")] string result_id,
        [Description("Directory to expand, relative to the result root, e.g. 'Season 01'.")] string? path = null,
        [Description("Glob relative to the result root, e.g. '**/*.flac' or 'Season 02/**'.")] string? glob = null,
        [Description("Entries to skip (paging).")] int? offset = null,
        CancellationToken cancellationToken = default) =>
        _run.Run("list_files", () => _files.ListAsync(result_id, path, glob, offset, cancellationToken));

    [McpServerTool(Name = "download", OpenWorld = true)]
    [Description("Starts downloading a result right away and returns job_id, the expected placement path and the rule that " +
                 "is expected to place it. Get the user's confirmation first. include/exclude need the file list; " +
                 "without them everything is downloaded.")]
    public Task<CallToolResult> Download(
        [Description("Result id from search.")] string result_id,
        [Description("Category id from get_categories.")] string category,
        [Description("Relative folder that replaces the release's root folder, following the category's path_hint, " +
                     "e.g. 'Show (2019)'. No '..', no leading '/', no <>:\"|?*.")] string? subpath = null,
        [Description("Glob patterns or exact paths (relative to the result root) to download, e.g. 'Season 02/**'.")] string[]? include = null,
        [Description("Glob patterns or exact paths to skip, applied after include, e.g. '**/*sample*'.")] string[]? exclude = null,
        CancellationToken cancellationToken = default) =>
        _run.Run("download", () => _downloads.DownloadAsync(result_id, category, subpath, include, exclude, cancellationToken));

    [McpServerTool(Name = "get_jobs", ReadOnly = true)]
    [Description("Lists jobs, newest first, with status (queued, downloading, stalled, placing, completed, failed, cancelled), " +
                 "progress, speed and path. Active jobs are read from their download client directly. 'completed' means the " +
                 "files are in place.")]
    public Task<CallToolResult> GetJobs(
        [Description("Only jobs with this status.")] string? status = null,
        [Description("Maximum number of jobs (default 20, at most 100).")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        _run.Run("get_jobs", () => _catalog.GetJobsAsync(status, limit, cancellationToken));

    [McpServerTool(Name = "cancel_job", Destructive = false)]
    [Description("Cancels a job: stops its download. Downloaded data and created links are kept.")]
    public Task<CallToolResult> CancelJob(
        [Description("Job id, e.g. j_42.")] string job_id,
        CancellationToken cancellationToken = default) =>
        _run.Run("cancel_job", () => _catalog.CancelJobAsync(job_id, cancellationToken));
}
