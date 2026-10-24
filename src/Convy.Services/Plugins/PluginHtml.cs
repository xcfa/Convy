using AngleSharp.Dom;
using Convy.Sources;
using HtmlParserOptions = AngleSharp.Html.Parser.HtmlParserOptions;

namespace Convy.Services.Plugins;

/// <summary>
/// <see cref="IPluginHtml"/> on AngleSharp: the HTML5 parsing algorithm, so malformed markup reads as in a browser, and
/// CSS selectors. Plugins see only the Abstractions interfaces, never AngleSharp's types. A bad selector is a
/// <see cref="SourceException"/>.
/// </summary>
public sealed class PluginHtml : IPluginHtml
{
    private readonly AngleSharp.Html.Parser.HtmlParser _parser = new(new HtmlParserOptions { IsScripting = false });

    public IPluginElement Parse(string html, Uri? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = _parser.ParseDocument(html);
        return new Element(document.DocumentElement, baseUrl);
    }

    private sealed class Element(IElement element, Uri? baseUrl) : IPluginElement
    {
        public string TagName => element.LocalName;

        public string Text => element.TextContent;

        public string InnerHtml => element.InnerHtml;

        public string? GetAttribute(string name) => element.GetAttribute(name);

        public Uri? GetUrl(string attribute)
        {
            var value = element.GetAttribute(attribute)?.Trim();
            if (string.IsNullOrEmpty(value))
                return null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
                return absolute;
            return baseUrl is not null && Uri.TryCreate(baseUrl, value, out var resolved) && resolved.Scheme is "http" or "https" ? resolved : null;
        }

        public IPluginElement? QuerySelector(string selector) =>
            Select(() => element.QuerySelector(selector)) is { } found ? Wrap(found) : null;

        public IReadOnlyList<IPluginElement> QuerySelectorAll(string selector) =>
            Select(() => element.QuerySelectorAll(selector).Select(Wrap).ToList());

        private IPluginElement Wrap(IElement child) => new Element(child, baseUrl);

        private static T Select<T>(Func<T> query)
        {
            try
            {
                return query();
            }
            catch (DomException ex)
            {
                throw new SourceException(SourceErrorKind.Error, $"Bad CSS selector: {ex.Message}", ex);
            }
        }
    }
}
