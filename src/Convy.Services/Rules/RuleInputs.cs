using Convy.Services.Downloads;

namespace Convy.Services.Rules;

/// <summary>Builds the property bag the routing rules are evaluated against.</summary>
public static class RuleInputs
{
    /// <summary>
    /// The downloader's properties plus <c>Provider</c>, which every item has. When the item
    /// belongs to an agent job, <paramref name="jobCategory"/> (the job's client category)
    /// overrides <c>Category</c>, so one category rule works for every protocol, including
    /// downloaders without categories of their own.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> For(DownloadItem item, string? jobCategory = null)
    {
        var properties = new Dictionary<string, object?>(item.Properties, StringComparer.OrdinalIgnoreCase)
        {
            ["Provider"] = item.Provider,
        };

        if (jobCategory is not null)
        {
            properties["Category"] = jobCategory;
        }

        return properties;
    }
}
