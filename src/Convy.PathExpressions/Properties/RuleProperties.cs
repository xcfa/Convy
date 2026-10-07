namespace Convy.PathExpressions.Properties;

/// <summary>
/// The registry of property names recognised by the filter DSL. Lookups are
/// case-insensitive; <see cref="RuleProperty.Name"/> is the canonical casing under which
/// downloaders publish values in an item's property bag.
///
/// The set is the union of what all downloaders can provide. A downloader publishes only
/// the properties it knows about, so an individual item may lack some of them; a rule
/// that references a property the item lacks is skipped as a whole
/// (<see cref="Mappings.MappingRule.Matches"/>). <c>Provider</c> is present on every item.
///
/// Value conventions: numbers are compared as <see cref="double"/>, durations in whole
/// seconds and timestamps as unix seconds (e.g. <c>SeedingTime &gt; 86400</c>). The torrent
/// <c>State</c> is matched by its name, e.g. <c>State == StalledUpload</c>.
/// </summary>
public static class RuleProperties
{
    private static readonly Dictionary<string, RuleProperty> Map =
        new(StringComparer.OrdinalIgnoreCase);

    static RuleProperties()
    {
        // ---- numbers ----------------------------------------------------------
        Num("AmountLeft");
        Num("Availability");
        Num("Completed");
        Num("DlLimit");
        Num("DownloadSpeed");
        Num("Downloaded");
        Num("DownloadedSession");
        Num("MaxRatio");
        Num("NumComplete");
        Num("NumIncomplete");
        Num("NumLeechs");
        Num("NumSeeds");
        Num("Priority");
        Num("Progress");
        Num("Ratio");
        Num("RatioLimit");
        Num("Size");
        Num("TotalSize");
        Num("UpLimit");
        Num("Uploaded");
        Num("UploadedSession");
        Num("UploadSpeed");

        // ---- durations (seconds) ----------------------------------------------
        Num("EstimatedTimeArrival");
        Num("MaxSeedingTime");
        Num("ReannounceTime");
        Num("SeedingTime");
        Num("SeedingTimeLimit");
        Num("TimeActive");

        // ---- timestamps (unix seconds) ----------------------------------------
        Num("AddedOn");
        Num("CompletionOn");
        Num("LastActivity");
        Num("SeenComplete");

        // ---- strings ----------------------------------------------------------
        Str("Provider"); // downloader owning the item: qbittorrent, slskd
        Str("Category");
        Str("ContentPath");
        Str("Hash");
        Str("MagnetUri");
        Str("Name");
        Str("SavePath");
        Str("Tracker");
        Str("State");

        // ---- booleans ---------------------------------------------------------
        Bool("AutoTmmEnabled");
        Bool("FirstLastPiecePriorityEnabled");
        Bool("ForceStartEnabled");
        Bool("PrivateEnabled");
        Bool("SequentialDownloadEnabled");
        Bool("SuperSeedingEnabled");

        // ---- collections ------------------------------------------------------
        Coll("TagList");
        Coll("Tags"); // friendly alias used in mapping files
    }

    /// <summary>Looks up a property descriptor by name, or <c>null</c> if unknown.</summary>
    public static RuleProperty? Find(string name) =>
        Map.TryGetValue(name, out var prop) ? prop : null;

    /// <summary>All recognised property names (canonical casing).</summary>
    public static IReadOnlyCollection<string> Names => Map.Keys;

    private static void Num(string name) => Add(name, PropertyKind.Numeric);

    private static void Str(string name) => Add(name, PropertyKind.Text);

    private static void Bool(string name) => Add(name, PropertyKind.Boolean);

    private static void Coll(string name) => Add(name, PropertyKind.Collection);

    private static void Add(string name, PropertyKind kind) =>
        Map[name] = new RuleProperty { Name = name, Kind = kind };
}
