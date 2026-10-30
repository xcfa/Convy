namespace Convy.Sources;

/// <summary>
/// The names of what a gallery source says about a release in <see cref="ContentInfo.Properties"/>: rules and the
/// REST API's clients (the Shizuka provider) read every site the same way. Values are strings; lists are joined with
/// <c>, </c>.
/// </summary>
public static class GalleryProperties
{
    /// <summary>The site's id of the release (a gallery <c>gid/token</c>, a MangaDex uuid).</summary>
    public const string Id = "id";

    /// <summary>The canonical address on the site.</summary>
    public const string Url = "url";

    public const string Title = "title";

    /// <summary>Another title (the original, a translation), the first the site gives.</summary>
    public const string OriginalTitle = "originalTitle";

    /// <summary>ISO 639-1 of the release's text.</summary>
    public const string Language = "language";

    public const string Description = "description";

    /// <summary>One line about a release found by a search (year, status, authors), when the full metadata is not read.</summary>
    public const string Summary = "summary";

    /// <summary>Tags as the site names them (<c>namespace:name</c> where it has namespaces), joined.</summary>
    public const string Tags = "tags";

    public const string Artists = "artists";

    public const string Authors = "authors";

    /// <summary>Translators and scanlation groups.</summary>
    public const string Translators = "translators";

    /// <summary>Circles and studios that made the work.</summary>
    public const string Groups = "groups";

    public const string Characters = "characters";

    /// <summary>The site's own category (Doujinshi, Manga, …) or content kind.</summary>
    public const string Category = "category";

    public const string Pages = "pages";

    /// <summary>When the site published it, <c>yyyy-MM-dd</c>.</summary>
    public const string Published = "published";

    public const string Uploader = "uploader";

    /// <summary>The site's rating, as the site writes it.</summary>
    public const string Rating = "rating";

    /// <summary>The age rating, in years (<c>0</c>, <c>12</c>, <c>16</c>, <c>18</c>).</summary>
    public const string AgeRating = "ageRating";

    public const string Cover = "cover";

    /// <summary>The series a chapter belongs to.</summary>
    public const string Series = "series";

    public const string Volume = "volume";

    public const string Chapter = "chapter";

    /// <summary>Whether the release is for adults (<c>true</c>/<c>false</c>).</summary>
    public const string Nsfw = "nsfw";
}
