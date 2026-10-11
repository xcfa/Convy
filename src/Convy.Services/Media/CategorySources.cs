using Convy.Sources;

namespace Convy.Services.Media;

/// <summary>Which sources a category searches.</summary>
public static class CategorySources
{
    /// <summary>
    /// The sources <paramref name="category"/> searches, in priority order: the sources it
    /// lists, else every active source by name. <paramref name="skipped"/> gets the listed ids
    /// that are missing or disabled.
    /// </summary>
    public static IReadOnlyList<IContentSource> Resolve(
        Category category, IReadOnlyList<IContentSource> all, out IReadOnlyList<string> skipped)
    {
        if (category.Sources is not { Count: > 0 } configured)
        {
            skipped = [];
            return all.Where(s => s.Status != SourceStatus.Disabled).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var selected = new List<IContentSource>();
        var missing = new List<string>();
        foreach (var id in configured.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var source = all.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase) && s.Status != SourceStatus.Disabled);
            if (source is null)
                missing.Add(id);
            else
                selected.Add(source);
        }

        skipped = missing;
        return selected;
    }
}
