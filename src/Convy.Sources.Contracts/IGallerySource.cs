namespace Convy.Sources;

/// <summary>
/// A source of galleries (manga and comic releases) that are downloaded page by page into a CBZ
/// (<see cref="Protocol.Gallery"/>). <see cref="IContentSource.ResolveAsync"/> gives a <see cref="GalleryPayload"/>;
/// the downloader then asks for the pages and the images through this interface, so every request goes through the
/// source's HTTP (its proxy, cookies and rate limit).
/// </summary>
public interface IGallerySource : IContentSource
{
    /// <summary>What the source is and can do, for clients that list the sources.</summary>
    GalleryManifest Manifest { get; }

    /// <summary>
    /// The release a link of the site points to, with its <see cref="ContentInfo.Properties"/>; <c>null</c> — the link
    /// is not one of this source, or the release does not exist. Throws <see cref="SourceException"/> when the site fails.
    /// </summary>
    Task<ContentInfo?> ResolveUrlAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>The pages of a release, in reading order.</summary>
    Task<IReadOnlyList<GalleryPage>> GetPagesAsync(string contentId, CancellationToken cancellationToken);

    /// <summary>
    /// The image of a page: <see cref="GalleryPage.ImageUrl"/> when the page came with one, else what the site answers
    /// for the page (e-hentai names the image on each page separately).
    /// </summary>
    Task<Uri> GetImageUrlAsync(GalleryPage page, CancellationToken cancellationToken);

    /// <summary>
    /// Another address for a page whose image at <paramref name="failed"/> could not be fetched (a broken mirror);
    /// <c>null</c> — the site offers none. Default: none.
    /// </summary>
    Task<Uri?> RetryImageUrlAsync(GalleryPage page, Uri failed, CancellationToken cancellationToken) => Task.FromResult<Uri?>(null);

    /// <summary>
    /// Opens the image at <paramref name="url"/> for reading, through the source's HTTP. The downloader copies the stream
    /// into the archive and disposes it. Throws <see cref="SourceException"/> for a refused or missing image.
    /// </summary>
    Task<GalleryImage> OpenImageAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// The release as one archive the site makes (a zip or CBZ), written into <paramref name="destination"/>, when the
    /// source has one for it (nhentai gives one to an account); <c>null</c> — none, the downloader takes the pages.
    /// Default: none.
    /// </summary>
    Task<GalleryArchive?> DownloadArchiveAsync(string contentId, Stream destination, CancellationToken cancellationToken) =>
        Task.FromResult<GalleryArchive?>(null);
}

/// <summary>An archive <see cref="IGallerySource.DownloadArchiveAsync"/> wrote.</summary>
/// <param name="Bytes">How many bytes it has.</param>
/// <param name="Pages">How many pages it holds, when the site says.</param>
public sealed record GalleryArchive(long Bytes, int? Pages);

/// <summary>One page of a gallery.</summary>
/// <param name="Number">1-based position in reading order.</param>
/// <param name="Url">The page as the site addresses it (a page of the site, or the image itself).</param>
/// <param name="ImageUrl">The image's address when the listing already names it; <c>null</c> — ask <see cref="IGallerySource.GetImageUrlAsync"/>.</param>
public sealed record GalleryPage(int Number, Uri Url, Uri? ImageUrl = null);

/// <summary>An opened image: its bytes and what the site says about them.</summary>
/// <param name="Content">The image's bytes; disposed by the reader.</param>
/// <param name="MediaType">The <c>Content-Type</c> of the answer (<c>image/jpeg</c>), when the site sends one.</param>
/// <param name="Length">The image's size in bytes, when the site sends one.</param>
public sealed record GalleryImage(Stream Content, string? MediaType, long? Length) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

/// <summary>What a gallery source says about itself, for clients that list the sources (the Shizuka provider).</summary>
public sealed record GalleryManifest
{
    /// <summary>The site's host names the source works with (<c>e-hentai.org</c>); a link on one of them is for <see cref="IGallerySource.ResolveUrlAsync"/>.</summary>
    public IReadOnlyList<string> Domains { get; init; } = [];

    /// <summary>Whether the site is for adults as a whole.</summary>
    public bool Nsfw { get; init; }

    /// <summary>ISO 639-1 codes of the languages the site mostly has, most common first.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>The age rating a release gets when the site says nothing (<c>0</c>, <c>12</c>, <c>16</c>, <c>18</c>); <c>null</c> — the client decides.</summary>
    public int? DefaultAgeRating { get; init; }

    /// <summary>Whether <see cref="IContentSource.SearchAsync"/> finds releases by text (some sites only resolve links).</summary>
    public bool CanSearch { get; init; } = true;

    /// <summary>Whether the source may have a release as one archive the site makes (<see cref="IGallerySource.DownloadArchiveAsync"/>).</summary>
    public bool Archives { get; init; }

    /// <summary>Filters a search accepts besides the text, in display order.</summary>
    public IReadOnlyList<GalleryFilter> Filters { get; init; } = [];
}

/// <summary>A filter of a gallery search: a key with the values it accepts.</summary>
/// <param name="Key">How a client passes it (<c>category</c>); <see cref="GallerySearch.Filters"/>.</param>
/// <param name="Name">What to show for it.</param>
/// <param name="Options">The accepted values with their names.</param>
/// <param name="Multiple">Whether several values may be chosen at once.</param>
public sealed record GalleryFilter(string Key, string Name, IReadOnlyList<GalleryFilterOption> Options, bool Multiple = true);

/// <summary>One value of a <see cref="GalleryFilter"/>.</summary>
public sealed record GalleryFilterOption(string Value, string Name);

/// <summary>
/// A search of a gallery source beyond one query line: the text, a language, filters and a page; what the REST API
/// passes on. A source that wants them reads <see cref="SearchRequest.Gallery"/>.
/// </summary>
/// <param name="Language">ISO 639-1 to prefer, or <c>null</c>.</param>
/// <param name="Filters">Chosen values by <see cref="GalleryFilter.Key"/>.</param>
/// <param name="Page">The page token the previous answer gave (<see cref="GallerySearchResult.NextPage"/>), or <c>null</c> for the first.</param>
public sealed record GallerySearch(string? Language, IReadOnlyDictionary<string, IReadOnlyList<string>> Filters, string? Page)
{
    public static GallerySearch Empty { get; } = new(null, new Dictionary<string, IReadOnlyList<string>>(), null);
}

/// <summary>One page of a gallery search: the releases found and how to ask for the next page.</summary>
/// <param name="Items">The releases, with their <see cref="ContentInfo.Properties"/>.</param>
/// <param name="NextPage">The token for the next page, or <c>null</c> when this was the last.</param>
public sealed record GallerySearchResult(IReadOnlyList<ContentInfo> Items, string? NextPage);

/// <summary>
/// Paged search of a gallery source for clients that browse (the REST API). Optional: a source without it is
/// searched through <see cref="IContentSource.SearchAsync"/>, one page.
/// </summary>
public interface IGalleryBrowser
{
    /// <summary>A page of releases for a query; an empty query lists the site's front page or latest.</summary>
    Task<GallerySearchResult> BrowseAsync(string? query, GallerySearch search, CancellationToken cancellationToken);
}
