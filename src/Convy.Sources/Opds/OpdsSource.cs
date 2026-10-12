using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Convy.Sources.Opds;

/// <summary>Offers every configured OPDS catalog as a source, id <c>opds:&lt;catalog id&gt;</c>.</summary>
public sealed class OpdsSourceProvider : ISourceProvider
{
    private readonly OpdsCatalogs _catalogs;
    private readonly ILogger<OpdsSource> _logger;

    public OpdsSourceProvider(OpdsCatalogs catalogs, ILogger<OpdsSource> logger)
    {
        _catalogs = catalogs;
        _logger = logger;
    }

    public string Name => "opds";

    public async Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        // Probed together: one slow catalog does not hold up the others.
        return await Task.WhenAll(_catalogs.All.Select(async client =>
        {
            var (status, message) = await ProbeAsync(client, cancellationToken).ConfigureAwait(false);
            return (IContentSource)new OpdsSource(client, status, message, _logger);
        })).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the root feed: the catalog is up when it answers with a feed. Allowed as long as a
    /// search (<c>timeout_sec</c>): a slow mirror is slow, not down, and the sources are listed in
    /// the background, so nobody waits for the probe.
    /// </summary>
    private static async Task<(SourceStatus, string?)> ProbeAsync(OpdsClient client, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(client.Catalog.Timeout);
        try
        {
            await client.GetFeedAsync(client.Catalog.Url, deadline.Token).ConfigureAwait(false);
            return (SourceStatus.Ok, null);
        }
        catch (SourceException ex)
        {
            return (SourceStatus.Error, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (SourceStatus.Error, $"{client.Catalog.Name} did not answer within {client.Catalog.Timeout.TotalSeconds:0} s.");
        }
    }
}

/// <summary>
/// An OPDS catalog as a source. A result is one book; its formats are its files. A search
/// asks the catalog for books by title and, when the catalog has an author search, for the
/// books of the authors whose name matches; results are returned as soon as either answers,
/// so a slow catalog still yields what arrived before the timeout.
/// </summary>
public sealed class OpdsSource : IContentSource
{
    /// <summary>Pages of a book search read for one query.</summary>
    private const int SearchPages = 2;

    private readonly OpdsClient _client;
    private readonly ILogger<OpdsSource> _logger;

    public OpdsSource(OpdsClient client, SourceStatus status, string? statusMessage, ILogger<OpdsSource> logger)
    {
        _client = client;
        Status = status;
        StatusMessage = statusMessage;
        _logger = logger;
    }

    private OpdsCatalog Catalog => _client.Catalog;

    public string Id => "opds:" + Catalog.Id;

    public string Name => Catalog.Name;

    public Protocol Protocol => Protocol.Opds;

    public SourceStatus Status { get; }

    public string? StatusMessage { get; }

    public TimeSpan? Timeout => Catalog.Timeout;

    public async IAsyncEnumerable<ContentInfo> SearchAsync(
        SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<IReadOnlyList<OpdsEntry>>> { SearchBooksAsync(request.Query, stop.Token) };
        if (Catalog.AuthorSearchUrl is not null)
        {
            pending.Add(SearchAuthorsBooksAsync(request.Query, stop.Token));
        }

        var searches = pending.Count;
        var failures = new List<SourceException>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);

                IReadOnlyList<OpdsEntry> entries;
                try
                {
                    entries = await done.ConfigureAwait(false);
                }
                catch (SourceException ex)
                {
                    failures.Add(ex);
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (OpdsBooks.ToBook(Catalog.Id, entry) is { } book && seen.Add(book.BookKey))
                    {
                        yield return ToContent(book);
                    }
                }
            }
        }
        finally
        {
            // Never leave a request running behind the caller's back.
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var task in pending)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SourceException)
                {
                    // Stopped on purpose.
                }
            }
        }

        if (failures.Count == searches)
        {
            throw failures[0];
        }

        foreach (var failure in failures)
        {
            _logger.LogInformation("Part of the {Catalog} search for '{Query}' failed: {Message}", Catalog.Name, request.Query, failure.Message);
        }
    }

    public Task<FileListing> ListFilesAsync(string contentId, CancellationToken cancellationToken)
    {
        var book = OpdsBookRef.Deserialize(contentId);
        var files = OpdsBooks.Files(book, Catalog.Formats, Catalog.UnzipFb2);
        return Task.FromResult(new FileListing(files.Select(f => new ListedFile(f.Name, f.Size ?? 0)).ToList()));
    }

    public Task<DownloadPayload> ResolveAsync(string contentId, CancellationToken cancellationToken)
    {
        var book = OpdsBookRef.Deserialize(contentId);
        DownloadPayload payload = new OpdsPayload(
            Catalog.Id,
            book.BookKey,
            OpdsBooks.FolderOf(book.Title, book.Authors),
            OpdsBooks.Files(book, Catalog.Formats, Catalog.UnzipFb2));
        return Task.FromResult(payload);
    }

    private ContentInfo ToContent(OpdsBookRef book) => new()
    {
        ContentId = OpdsBookRef.Serialize(book),
        Title = OpdsBooks.DisplayTitle(book, Catalog.Formats),
        SizeBytes = book.Size,
        FileCount = 1,
        DedupKey = $"opds:{Catalog.Id}:{book.BookKey}",
    };

    private async Task<IReadOnlyList<OpdsEntry>> SearchBooksAsync(string query, CancellationToken cancellationToken)
    {
        var template = Catalog.SearchUrl ?? await DiscoverSearchTemplateAsync(cancellationToken).ConfigureAwait(false);
        var books = new List<OpdsEntry>();
        Uri? next = Expand(template, query);

        for (var page = 0; page < SearchPages && next is not null; page++)
        {
            var feed = await _client.GetFeedAsync(next, cancellationToken).ConfigureAwait(false);
            books.AddRange(feed.Entries.Where(e => e.IsBook));
            next = feed.Next;
        }

        return books;
    }

    /// <summary>Books of the authors a query names, read from each author's feed.</summary>
    private async Task<IReadOnlyList<OpdsEntry>> SearchAuthorsBooksAsync(string query, CancellationToken cancellationToken)
    {
        var found = await _client.GetFeedAsync(Expand(Catalog.AuthorSearchUrl!, query), cancellationToken).ConfigureAwait(false);

        // Several namesakes may match a surname; the prolific ones are the likely target.
        var authors = found.Entries
            .Where(e => !e.IsBook && e.Links.Any(l => l.IsNavigation))
            .OrderByDescending(e => OpdsContent.BookCount(e.Content) ?? 0)
            .Take(Math.Max(1, Catalog.MaxAuthors))
            .ToList();

        var lists = await Task.WhenAll(authors.Select(a => ReadAuthorBooksAsync(a, cancellationToken))).ConfigureAwait(false);
        return lists.SelectMany(l => l).ToList();
    }

    private async Task<IReadOnlyList<OpdsEntry>> ReadAuthorBooksAsync(OpdsEntry author, CancellationToken cancellationToken)
    {
        var link = author.Links.First(l => l.IsNavigation).Href;
        var pages = Math.Max(1, Catalog.AuthorPages);

        if (Catalog.FlibustaAuthorPages)
        {
            // 20 books per page; read the pages the author has (up to the limit) at once.
            var count = OpdsContent.BookCount(author.Content);
            var needed = count is { } books ? Math.Min(pages, Math.Max(1, (books + 19) / 20)) : pages;
            var root = link.ToString().TrimEnd('/') + "/alphabet";
            var urls = Enumerable.Range(0, needed).Select(i => new Uri(i == 0 ? root : $"{root}/{i}"));

            var feeds = await Task.WhenAll(urls.Select(u => _client.GetFeedAsync(u, cancellationToken))).ConfigureAwait(false);
            return feeds.SelectMany(f => f.Entries.Where(e => e.IsBook)).ToList();
        }

        // Generic catalogs: the author's feed, or the first section of it, then its next pages.
        var feed = await _client.GetFeedAsync(link, cancellationToken).ConfigureAwait(false);
        if (!feed.Entries.Any(e => e.IsBook))
        {
            var section = feed.Entries
                .Where(e => e.Links.Any(l => l.IsNavigation))
                .OrderByDescending(e => e.Title.Contains("алфавит", StringComparison.OrdinalIgnoreCase)
                                        || e.Title.Contains("alphabet", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (section is null)
            {
                return [];
            }

            feed = await _client.GetFeedAsync(section.Links.First(l => l.IsNavigation).Href, cancellationToken).ConfigureAwait(false);
        }

        var entries = feed.Entries.Where(e => e.IsBook).ToList();
        for (var page = 1; page < pages && feed.Next is not null; page++)
        {
            feed = await _client.GetFeedAsync(feed.Next, cancellationToken).ConfigureAwait(false);
            entries.AddRange(feed.Entries.Where(e => e.IsBook));
        }

        return entries;
    }

    /// <summary>The search address the root feed announces (an Atom search link with <c>{searchTerms}</c>).</summary>
    private async Task<string> DiscoverSearchTemplateAsync(CancellationToken cancellationToken)
    {
        var root = await _client.GetFeedAsync(Catalog.Url, cancellationToken).ConfigureAwait(false);
        var search = root.Links.FirstOrDefault(l =>
            l.Rel == "search"
            && l.Type?.StartsWith("application/atom+xml", StringComparison.OrdinalIgnoreCase) == true
            && Uri.UnescapeDataString(l.Href.ToString()).Contains("{searchTerms}", StringComparison.Ordinal));

        return search is null
            ? throw new SourceException(SourceErrorKind.Error, $"{Catalog.Name} announces no search; set search_url for it.")
            : Uri.UnescapeDataString(search.Href.ToString());
    }

    private Uri Expand(string template, string query) =>
        new(Catalog.Url, template.Replace("{searchTerms}", Uri.EscapeDataString(query.Trim()), StringComparison.Ordinal));
}
