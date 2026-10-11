using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Convy.Sources.Opds;

/// <summary>A link of an OPDS feed or entry, with an absolute address.</summary>
public sealed record OpdsLink(Uri Href, string? Rel, string? Type, string? Title)
{
    /// <summary>Whether the link downloads the book (<c>http://opds-spec.org/acquisition…</c>).</summary>
    public bool IsAcquisition => Rel?.StartsWith("http://opds-spec.org/acquisition", StringComparison.Ordinal) == true;

    /// <summary>Whether the link leads to another catalog feed (a subsection).</summary>
    public bool IsNavigation =>
        !IsAcquisition
        && Type?.StartsWith("application/atom+xml", StringComparison.OrdinalIgnoreCase) == true
        && Rel is null or "subsection" or "http://opds-spec.org/sort/new" or "http://opds-spec.org/sort/popular";
}

/// <summary>An entry of an OPDS feed: a book (with acquisition links) or a navigation item (an author, a series).</summary>
public sealed record OpdsEntry(
    string? Id,
    string Title,
    IReadOnlyList<string> Authors,
    IReadOnlyList<OpdsLink> Links,
    string? Content,
    string? Language,
    string? Issued)
{
    public bool IsBook => Links.Any(l => l.IsAcquisition);
}

/// <summary>An OPDS (Atom) feed.</summary>
/// <param name="Next">The next page, when the feed is paged.</param>
public sealed record OpdsFeed(string? Title, IReadOnlyList<OpdsEntry> Entries, IReadOnlyList<OpdsLink> Links, Uri? Next);

/// <summary>Reads OPDS 1.x catalog feeds (Atom with Dublin Core terms).</summary>
public static class OpdsFeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/terms/";
    private static readonly XNamespace DcElements = "http://purl.org/dc/elements/1.1/";

    /// <summary>Parses a feed; relative links are resolved against <paramref name="baseUri"/>.</summary>
    public static OpdsFeed Parse(Stream xml, Uri baseUri)
    {
        XDocument document;
        try
        {
            // No DTDs: a catalog is remote input.
            using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, Async = false });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SourceException(SourceErrorKind.Error, $"The catalog did not return a valid OPDS feed: {ex.Message}", ex);
        }

        var feed = document.Root;
        if (feed is null || feed.Name != Atom + "feed")
        {
            throw new SourceException(SourceErrorKind.Error, "The catalog did not return an OPDS feed.");
        }

        var links = ParseLinks(feed, baseUri);
        var entries = feed.Elements(Atom + "entry").Select(e => ParseEntry(e, baseUri)).ToList();

        return new OpdsFeed(
            Text(feed.Element(Atom + "title")),
            entries,
            links,
            links.FirstOrDefault(l => l.Rel == "next")?.Href);
    }

    private static OpdsEntry ParseEntry(XElement entry, Uri baseUri) => new(
        Text(entry.Element(Atom + "id")),
        Text(entry.Element(Atom + "title")) ?? string.Empty,
        entry.Elements(Atom + "author").Select(a => Text(a.Element(Atom + "name"))).OfType<string>().ToList(),
        ParseLinks(entry, baseUri),
        Text(entry.Element(Atom + "content")) ?? Text(entry.Element(Atom + "summary")),
        Text(entry.Element(Dc + "language")) ?? Text(entry.Element(DcElements + "language")),
        Text(entry.Element(Dc + "issued")) ?? Text(entry.Element(DcElements + "date")));

    private static List<OpdsLink> ParseLinks(XElement element, Uri baseUri)
    {
        var links = new List<OpdsLink>();
        foreach (var link in element.Elements(Atom + "link"))
        {
            var href = (string?)link.Attribute("href");
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href, out var absolute))
            {
                continue;
            }

            links.Add(new OpdsLink(absolute, (string?)link.Attribute("rel"), (string?)link.Attribute("type"), (string?)link.Attribute("title")));
        }

        return links;
    }

    private static string? Text(XElement? element)
    {
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

/// <summary>Book details some catalogs (Flibusta and its engine) only put into the entry's HTML content.</summary>
public static partial class OpdsContent
{
    /// <summary>The size line, e.g. <c>Размер: 971 Kb</c>, in bytes.</summary>
    public static long? Size(string? content)
    {
        var match = SizePattern().Match(content ?? string.Empty);
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var factor = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "kb" or "кб" => 1024L,
            "mb" or "мб" => 1024L * 1024,
            _ => 1L,
        };
        return (long)(value * factor);
    }

    /// <summary>A number of books, e.g. <c>345 книг</c> in an author entry.</summary>
    public static int? BookCount(string? content)
    {
        var match = BookCountPattern().Match(content ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : null;
    }

    [GeneratedRegex(@"(?:Размер|Size):\s*([\d.,]+)\s*(Kb|Mb|Кб|Мб|b)?", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"^\s*(\d+)\s+(?:книг|books?)", RegexOptions.IgnoreCase)]
    private static partial Regex BookCountPattern();
}
