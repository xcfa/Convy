namespace Convy.Sources;

/// <summary>Availability of a source as shown to the agent.</summary>
public enum SourceStatus
{
    /// <summary>Usable.</summary>
    Ok,

    /// <summary>Currently failing (blocked indexer, peer network down, …).</summary>
    Error,

    /// <summary>Switched off in its backend.</summary>
    Disabled,
}

/// <summary>
/// Returns the sources a backend offers: Prowlarr offers one per indexer, slskd a single one.
/// </summary>
public interface ISourceProvider
{
    /// <summary>Name of the provider, for logs (<c>prowlarr</c>, <c>slskd</c>).</summary>
    string Name { get; }

    Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What the agent sees as a source: a tracker (Prowlarr indexer) or Soulseek. A source finds
/// content, lists its files and turns it into a <see cref="DownloadPayload"/>; it does not
/// download anything itself.
/// </summary>
public interface IContentSource
{
    /// <summary>Stable id, e.g. <c>prowlarr:12</c> or <c>soulseek</c>.</summary>
    string Id { get; }

    string Name { get; }

    Protocol Protocol { get; }

    SourceStatus Status { get; }

    /// <summary>Why the source is not <see cref="SourceStatus.Ok"/>, when known.</summary>
    string? StatusMessage { get; }

    /// <summary>
    /// Searches for one query variant. Throws <see cref="SourceException"/> when the source
    /// fails, so a failure is never confused with an empty result.
    /// </summary>
    IAsyncEnumerable<ContentInfo> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    /// <summary>Lists every file of a found content (<see cref="ContentInfo.ContentId"/>).</summary>
    Task<FileListing> ListFilesAsync(string contentId, CancellationToken cancellationToken);

    /// <summary>Turns a found content into what its downloader needs.</summary>
    Task<DownloadPayload> ResolveAsync(string contentId, CancellationToken cancellationToken);
}

/// <summary>One query variant within a category.</summary>
/// <param name="Query">The query text as the agent wrote it.</param>
/// <param name="Category">Category settings for the providers.</param>
public sealed record SearchRequest(string Query, CategorySearchSettings Category);

/// <summary>
/// The provider-specific part of a category. Each provider reads only its own fields.
/// </summary>
/// <param name="Id">Category id (<c>movies</c>, <c>music</c>, …).</param>
/// <param name="ProwlarrCategories">Newznab category ids passed to Prowlarr.</param>
/// <param name="SoulseekExtensions">File extensions (without dot) kept from Soulseek results.</param>
public sealed record CategorySearchSettings(
    string Id,
    IReadOnlyList<int> ProwlarrCategories,
    IReadOnlyList<string> SoulseekExtensions);

/// <summary>One found unit: a torrent release, or a Soulseek user's folder.</summary>
public sealed record ContentInfo
{
    /// <summary>Opaque, source-specific reference used for listing and resolving.</summary>
    public required string ContentId { get; init; }

    public required string Title { get; init; }

    public long? SizeBytes { get; init; }

    public int? FileCount { get; init; }

    /// <summary>
    /// Identity across sources and query variants (e.g. <c>btih:&lt;hash&gt;</c>), or <c>null</c>
    /// when the content cannot be recognised elsewhere.
    /// </summary>
    public string? DedupKey { get; init; }

    public Availability Availability { get; init; } = new();
}

/// <summary>How available a content is; torrents fill the swarm fields, Soulseek the peer fields.</summary>
public sealed record Availability
{
    public int? Seeders { get; init; }
    public int? Leechers { get; init; }
    public bool? FreeUploadSlot { get; init; }
    public int? QueueLength { get; init; }
    public long? UploadSpeed { get; init; }
}

/// <summary>Every file of a content.</summary>
/// <param name="Files">Files with paths relative to the content root ('/'-separated).</param>
/// <param name="TorrentFile">
/// Raw .torrent bytes when the list came from torrent metadata; lets the downloader select
/// files before anything is fetched.
/// </param>
public sealed record FileListing(IReadOnlyList<ListedFile> Files, byte[]? TorrentFile = null);

/// <summary>A file of a content.</summary>
public sealed record ListedFile(string Path, long Size);

/// <summary>Why a source failed.</summary>
public enum SourceErrorKind
{
    /// <summary>The backend rejected the credentials or the session (expired cookies, captcha).</summary>
    AuthFailed,

    /// <summary>Any other failure.</summary>
    Error,
}

/// <summary>A source failed; distinguishes failures from empty results.</summary>
public sealed class SourceException : Exception
{
    public SourceException(SourceErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public SourceErrorKind Kind { get; }
}
