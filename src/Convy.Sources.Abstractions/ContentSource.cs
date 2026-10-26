using Shizuka.Sources.Abstractions;

namespace Convy.Sources.Abstractions;

/// <summary>
/// A source of Convy's that is not a site: it finds releases and says how to fetch them (a torrent, a Soulseek user's
/// folder, files at addresses), and Convy's downloaders fetch them. A public non-abstract subclass with a public
/// parameterless constructor in an extension is found and created once, when the extension loads.
/// </summary>
/// <remarks>
/// Every call gets an <see cref="ISourceContext"/> made for it, as a site extension does: HTTP limited to the
/// manifest's <see cref="SourceManifest.Domains"/> and the hosts of its <see cref="SourceSettingKind.Url"/> settings
/// (a service of the user's, such as <c>http://slskd:5030</c>), the saved settings, the log. The manifest's
/// <see cref="SourceManifest.Capabilities"/> mean nothing here. Failures are <see cref="SourceException"/>s; an empty
/// result is not one.
/// </remarks>
public abstract class ContentSource
{
    /// <summary>Who the source is: id, name, domains, settings, rate limit.</summary>
    public abstract SourceManifest Manifest { get; }

    /// <summary>How its releases are fetched: the kind of <see cref="ContentRelease"/> <see cref="ResolveAsync"/> returns.</summary>
    public abstract ContentProtocol Protocol { get; }

    /// <summary>How long one call may take when it needs longer than the host's default (a slow catalog); <c>null</c> — the default.</summary>
    public virtual TimeSpan? Timeout => null;

    /// <summary>
    /// Whether the source can be used now (set up, signed in, its service reachable): <c>null</c> — yes; otherwise why
    /// not, shown as the source's error. Asked when the sources are listed.
    /// </summary>
    public virtual Task<string?> CheckAsync(ISourceContext context, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>The releases for one query.</summary>
    public abstract Task<IReadOnlyList<ContentItem>> SearchAsync(ContentQuery query, ISourceContext context, CancellationToken ct);

    /// <summary>
    /// The files of a found release (<see cref="ContentItem.ContentId"/>); <c>null</c> (the default) — Convy reads them
    /// from <see cref="ResolveAsync"/>'s release: a torrent's metadata, a folder's or a book's files.
    /// </summary>
    public virtual Task<IReadOnlyList<ContentFile>?> ListFilesAsync(string contentId, ISourceContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ContentFile>?>(null);

    /// <summary>What a downloader needs to fetch a found release; of the kind <see cref="Protocol"/> names.</summary>
    public abstract Task<ContentRelease> ResolveAsync(string contentId, ISourceContext context, CancellationToken ct);

    /// <summary>
    /// Headers Convy sends when it fetches the addresses of a release itself (a <see cref="TorrentRelease.TorrentUrl"/>,
    /// the files of a <see cref="FilesRelease"/>): a sign-in or a key from the settings. Asked for each fetch, so a
    /// download that runs later sends the settings saved then. <c>null</c> (the default) — none; the source's cookies
    /// go anyway.
    /// </summary>
    public virtual IReadOnlyDictionary<string, string>? FetchHeaders(ISourceContext context) => null;
}

/// <summary>
/// Lists sources that a service of the user's has (a torrent indexer of Prowlarr each): its manifest's settings are
/// the service's (address, key); the sources it lists get its context and their own manifests (their ids unique and not
/// the provider's). Asked when the sources are listed.
/// </summary>
public abstract class ContentSourceProvider
{
    public abstract SourceManifest Manifest { get; }

    public abstract Task<IReadOnlyList<ContentSource>> GetSourcesAsync(ISourceContext context, CancellationToken ct);
}

/// <summary>How a release is fetched.</summary>
public enum ContentProtocol
{
    /// <summary>BitTorrent: a <see cref="TorrentRelease"/>, added to qBittorrent.</summary>
    Torrent,

    /// <summary>Soulseek: a <see cref="SoulseekRelease"/>, queued in slskd.</summary>
    Soulseek,

    /// <summary>Files at addresses: a <see cref="FilesRelease"/>, fetched by Convy itself (an OPDS catalog's book).</summary>
    Files,
}

/// <summary>One search.</summary>
public sealed record ContentQuery
{
    /// <summary>The query as the user or the agent wrote it.</summary>
    public required string Text { get; init; }

    /// <summary>Convy's category (<c>music</c>, <c>books</c>); <c>null</c> — none.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// The category's options for this source, from <c>categories.&lt;id&gt;.options.&lt;source or provider id&gt;</c> of
    /// Convy's configuration: lists of values by key (Prowlarr's <c>categories</c>, Soulseek's <c>extensions</c>). Empty
    /// — none.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Options { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The values of option <paramref name="key"/>; empty — none.</summary>
    public IReadOnlyList<string> Option(string key) => Options.TryGetValue(key, out var values) ? values : [];
}

/// <summary>A found release.</summary>
public sealed record ContentItem
{
    /// <summary>What <see cref="ContentSource.ListFilesAsync"/> and <see cref="ContentSource.ResolveAsync"/> get back: opaque to Convy.</summary>
    public required string ContentId { get; init; }

    public required string Title { get; init; }

    public long? SizeBytes { get; init; }

    public int? FileCount { get; init; }

    /// <summary>The release's identity across sources and queries (<c>btih:&lt;hash&gt;</c>); <c>null</c> — none.</summary>
    public string? DedupKey { get; init; }

    /// <summary>The release's page for a person (not a download link); <c>null</c> — none.</summary>
    public Uri? InfoUrl { get; init; }

    /// <summary>Torrent swarm: seeders.</summary>
    public int? Seeders { get; init; }

    /// <summary>Torrent swarm: leechers.</summary>
    public int? Leechers { get; init; }

    /// <summary>Soulseek: the peer has a free upload slot.</summary>
    public bool? FreeUploadSlot { get; init; }

    /// <summary>Soulseek: the peer's queue.</summary>
    public int? QueueLength { get; init; }

    /// <summary>Soulseek: the peer's upload speed, bytes per second.</summary>
    public long? UploadSpeed { get; init; }

    /// <summary>What else the source knows (tags, language, cover…), shown to the REST API's clients as is.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

/// <summary>A file of a release: its path relative to the release ('/'-separated) and size.</summary>
public sealed record ContentFile(string Path, long Size);

/// <summary>How to fetch a release: one of <see cref="TorrentRelease"/>, <see cref="SoulseekRelease"/>, <see cref="FilesRelease"/>.</summary>
public abstract record ContentRelease;

/// <summary>
/// A torrent: the address of its .torrent, or its magnet link, or both (the address is tried first); the info hash
/// when known. Convy fetches the address with the source's rules (its domains, cookies, proxy,
/// <see cref="ContentSource.FetchHeaders"/>; an answer that redirects to a <c>magnet:</c> link is that link) and reads
/// the files from the torrent's metadata.
/// </summary>
public sealed record TorrentRelease : ContentRelease
{
    public string? Magnet { get; init; }

    public Uri? TorrentUrl { get; init; }

    /// <summary>The v1 info hash in lowercase hex, when known; with neither link the magnet is made from it.</summary>
    public string? InfoHash { get; init; }
}

/// <summary>A Soulseek user's folder: the files to queue, by their full remote names (backslash-separated).</summary>
public sealed record SoulseekRelease(string Username, string Directory, IReadOnlyList<ContentFile> Files) : ContentRelease;

/// <summary>
/// Files Convy fetches itself into <paramref name="Folder"/> (an OPDS book: one file per format), each with the
/// source's rules (its domains, cookies, proxy, <see cref="ContentSource.FetchHeaders"/>).
/// </summary>
/// <param name="Folder">The folder's name, e.g. <c>Стругацкий - Пикник на обочине</c>.</param>
/// <param name="Files">The files: at least one.</param>
public sealed record FilesRelease(string Folder, IReadOnlyList<FileLink> Files) : ContentRelease;

/// <summary>
/// One file of a <see cref="FilesRelease"/>.
/// </summary>
/// <param name="Name">The file's name as stored.</param>
/// <param name="Url">Its address.</param>
/// <param name="Size">Its size when known.</param>
/// <param name="Unzip">The site sends it zipped (<c>.fb2.zip</c>): the single file inside is stored.</param>
/// <param name="Preference">
/// Rank among the formats the user prefers (0 — the most); <c>null</c> — not preferred. Without a selection the best
/// ranked file is fetched, and the next one if it fails.
/// </param>
public sealed record FileLink(string Name, Uri Url, long? Size = null, bool Unzip = false, int? Preference = null);
