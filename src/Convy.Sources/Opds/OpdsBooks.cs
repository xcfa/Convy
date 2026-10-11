using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Convy.Sources.Opds;

/// <summary>A downloadable format of a book.</summary>
/// <param name="Format">Format key the preferences use: <c>epub</c>, <c>fb2</c>, <c>mobi</c>, <c>pdf</c>, …</param>
/// <param name="Extension">File extension as sent (<c>fb2.zip</c> for a zipped FB2).</param>
/// <param name="Zipped">The file is a zip around the actual book (<c>.fb2.zip</c>).</param>
public sealed record OpdsFormat(string Format, string Extension, bool Zipped);

/// <summary>What Convy keeps about a found book to list and download it later (the result's content id).</summary>
public sealed record OpdsBookRef(
    string CatalogId,
    string BookKey,
    string Title,
    IReadOnlyList<string> Authors,
    string? Year,
    string? Language,
    long? Size,
    IReadOnlyList<OpdsBookLink> Links)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(OpdsBookRef book) => JsonSerializer.Serialize(book, Json);

    public static OpdsBookRef Deserialize(string contentId) =>
        JsonSerializer.Deserialize<OpdsBookRef>(contentId, Json)
        ?? throw new SourceException(SourceErrorKind.Error, "Corrupted content reference.");
}

/// <summary>An acquisition link of a book.</summary>
public sealed record OpdsBookLink(string Url, string? Type);

/// <summary>Turns OPDS entries into books: formats, keys, names.</summary>
public static partial class OpdsBooks
{
    private static readonly Dictionary<string, OpdsFormat> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/epub+zip"] = new("epub", "epub", false),
        ["application/fb2+zip"] = new("fb2", "fb2.zip", true),
        ["application/x-zip-compressed-fb2"] = new("fb2", "fb2.zip", true),
        ["application/fb2"] = new("fb2", "fb2", false),
        ["application/x-fictionbook+xml"] = new("fb2", "fb2", false),
        ["text/fb2+xml"] = new("fb2", "fb2", false),
        ["application/x-mobipocket-ebook"] = new("mobi", "mobi", false),
        ["application/x-mobi8-ebook"] = new("azw3", "azw3", false),
        ["application/pdf"] = new("pdf", "pdf", false),
        ["image/vnd.djvu"] = new("djvu", "djvu", false),
        ["image/x-djvu"] = new("djvu", "djvu", false),
        ["application/x-cbz"] = new("cbz", "cbz", false),
        ["application/vnd.comicbook+zip"] = new("cbz", "cbz", false),
        ["application/html+zip"] = new("html", "html.zip", false),
        ["application/txt+zip"] = new("txt", "txt.zip", false),
        ["application/rtf+zip"] = new("rtf", "rtf.zip", false),
        ["text/plain"] = new("txt", "txt", false),
    };

    /// <summary>The format of an acquisition link, from its MIME type or, failing that, its address.</summary>
    public static OpdsFormat FormatOf(string? type, string url)
    {
        var mime = type?.Split(';')[0].Trim();
        if (mime is not null && Types.TryGetValue(mime, out var known))
        {
            return known;
        }

        var extension = Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (extension.Length is > 0 and <= 5 && extension.All(char.IsLetterOrDigit))
        {
            return new OpdsFormat(extension, extension, false);
        }

        // e.g. "application/x-something": the subtype without the "x-" prefix.
        var subtype = mime?.Split('/').LastOrDefault()?.Replace("x-", string.Empty, StringComparison.Ordinal) ?? "bin";
        var plain = new string(subtype.TakeWhile(char.IsLetterOrDigit).ToArray());
        return new OpdsFormat(plain.Length > 0 ? plain : "bin", plain.Length > 0 ? plain : "bin", false);
    }

    /// <summary>
    /// A file-system safe key of a book: <c>b93857</c> for Flibusta-style links (<c>/b/93857/…</c>),
    /// otherwise a hash of the entry's id or first link.
    /// </summary>
    public static string KeyOf(OpdsEntry entry)
    {
        foreach (var link in entry.Links.Where(l => l.IsAcquisition || l.Rel == "alternate"))
        {
            var match = FlibustaBookPattern().Match(link.Href.AbsolutePath);
            if (match.Success)
            {
                return "b" + match.Groups[1].Value;
            }
        }

        var identity = entry.Id ?? entry.Links.FirstOrDefault(l => l.IsAcquisition)?.Href.ToString() ?? entry.Title;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }

    /// <summary>The folder a book is stored in: <c>Author - Title</c>, safe as a path segment.</summary>
    public static string FolderOf(string title, IReadOnlyList<string> authors)
    {
        var name = authors.Count > 0 ? $"{authors[0]} - {title}" : title;
        var safe = UnsafeCharacters().Replace(name, " ");
        safe = Spaces().Replace(safe, " ").Trim().TrimEnd('.');
        if (safe.Length > 150)
        {
            safe = safe[..150].TrimEnd();
        }

        return safe.Length > 0 ? safe : "book";
    }

    /// <summary>The name a format of a book is stored under; a zipped FB2 that is unpacked loses its <c>.zip</c>.</summary>
    public static string FileName(string folder, OpdsFormat format, bool unzipFb2) =>
        $"{folder}.{(format.Zipped && unzipFb2 ? format.Format : format.Extension)}";

    /// <summary>The book of an entry with acquisition links, or <c>null</c> for any other entry.</summary>
    public static OpdsBookRef? ToBook(string catalogId, OpdsEntry entry)
    {
        if (!entry.IsBook)
        {
            return null;
        }

        var links = entry.Links
            .Where(l => l.IsAcquisition && l.Href.Scheme is "http" or "https")
            .GroupBy(l => FormatOf(l.Type, l.Href.ToString()).Format, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(l => new OpdsBookLink(l.Href.ToString(), l.Type))
            .ToList();

        var year = entry.Issued is { Length: >= 4 } issued && issued[..4].All(char.IsDigit) ? issued[..4] : null;
        return new OpdsBookRef(
            catalogId,
            KeyOf(entry),
            entry.Title,
            entry.Authors,
            year,
            entry.Language,
            OpdsContent.Size(entry.Content),
            links);
    }

    /// <summary>The book's files, preferred formats first, each with its rank among <paramref name="preferred"/>.</summary>
    public static IReadOnlyList<OpdsFile> Files(OpdsBookRef book, IReadOnlyList<string> preferred, bool unzipFb2)
    {
        var folder = FolderOf(book.Title, book.Authors);
        return book.Links
            .Select(l => (Link: l, Format: FormatOf(l.Type, l.Url)))
            .Select(x =>
            {
                // The catalog's size line describes its own format (FB2 for Flibusta).
                long? size = x.Format.Format == "fb2" ? book.Size : null;
                return new OpdsFile(
                    FileName(folder, x.Format, unzipFb2), x.Link.Url, size, x.Format.Zipped && unzipFb2, Rank(x.Format.Format, preferred));
            })
            .OrderBy(f => f.Preference ?? int.MaxValue)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The line the agent sees: <c>Authors — Title (year, language) [formats]</c>.</summary>
    public static string DisplayTitle(OpdsBookRef book, IReadOnlyList<string> preferred)
    {
        var authors = book.Authors.Count switch
        {
            0 => null,
            1 or 2 => string.Join(", ", book.Authors),
            _ => $"{book.Authors[0]}, {book.Authors[1]} +{book.Authors.Count - 2}",
        };

        var details = string.Join(", ", new[] { book.Year, book.Language }.Where(d => !string.IsNullOrEmpty(d)));
        var formats = string.Join("/", book.Links
            .Select(l => FormatOf(l.Type, l.Url).Format)
            .OrderBy(f => Rank(f, preferred) ?? int.MaxValue));

        var title = authors is null ? book.Title : $"{authors} — {book.Title}";
        if (details.Length > 0)
        {
            title += $" ({details})";
        }

        return formats.Length > 0 ? $"{title} [{formats}]" : title;
    }

    /// <summary>The position of <paramref name="format"/> among the preferred formats, or <c>null</c>.</summary>
    public static int? Rank(string format, IReadOnlyList<string> preferred)
    {
        for (var i = 0; i < preferred.Count; i++)
        {
            if (string.Equals(preferred[i], format, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^/b/(\d+)(?:/|$)")]
    private static partial Regex FlibustaBookPattern();

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")]
    private static partial Regex UnsafeCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
