using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Media;

/// <summary>A validated category.</summary>
public sealed record Category(
    string Id,
    string? Description,
    string? PathHint,
    string? ClientCategory,
    IReadOnlyList<string>? Sources,
    CategorySearchSettings SearchSettings);

/// <summary>
/// The configured categories. The configuration is reloaded when the file changes; a
/// version without the mandatory <c>other</c> category is rejected and the previous one
/// stays in effect.
/// </summary>
public sealed class CategoryCatalog : IDisposable
{
    public const string FallbackCategory = "other";

    private readonly ILogger<CategoryCatalog> _logger;
    private readonly IDisposable? _subscription;
    private volatile IReadOnlyDictionary<string, Category> _current = new Dictionary<string, Category>();

    public CategoryCatalog(IOptionsMonitor<CategoriesOptions> options, ILogger<CategoryCatalog> logger)
    {
        _logger = logger;
        Apply(options.CurrentValue);
        _subscription = options.OnChange(Apply);
    }

    /// <summary>Categories in id order.</summary>
    public IReadOnlyList<Category> All => _current.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

    /// <summary>Returns the category, or throws <see cref="ConvyRequestException"/> listing the available ones.</summary>
    public Category Get(string? id)
    {
        if (id is not null && _current.TryGetValue(id, out var category))
        {
            return category;
        }

        var available = _current.Count == 0
            ? "none are configured (check the categories section of configuration.yml)"
            : string.Join(", ", _current.Keys.Order(StringComparer.Ordinal));
        throw new ConvyRequestException($"Unknown category '{id}'. Available categories: {available}.");
    }

    public void Dispose() => _subscription?.Dispose();

    private void Apply(CategoriesOptions options)
    {
        var categories = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in options.Categories)
        {
            categories[id] = new Category(
                id,
                value.Description,
                value.PathHint,
                value.QbittorrentCategory,
                value.Sources?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList(),
                new CategorySearchSettings(
                    id,
                    value.Prowlarr?.Categories ?? [],
                    value.Soulseek?.Extensions?.Select(e => e.TrimStart('.').ToLowerInvariant()).ToList() ?? []));
        }

        if (categories.Count == 0 && _current.Count == 0)
        {
            // Not using search at all is a valid setup; nothing to complain about.
            _logger.LogInformation("No categories configured; MCP search and download are unavailable.");
            return;
        }

        if (!categories.ContainsKey(FallbackCategory))
        {
            _logger.LogError(
                "The categories configuration has no '{Fallback}' category and is not applied; {Count} previous categor(ies) stay in effect.",
                FallbackCategory, _current.Count);
            return;
        }

        _current = categories;
        _logger.LogInformation("Loaded {Count} categor(ies): {Ids}.", categories.Count, string.Join(", ", categories.Keys));
    }
}
