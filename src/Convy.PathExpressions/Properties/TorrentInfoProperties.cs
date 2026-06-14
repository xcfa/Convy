using Banned.Qbittorrent.Models.Torrent;

namespace Convy.PathExpressions.Properties;

/// <summary>
/// The registry of property names recognised by the filter DSL, mapped to typed
/// accessors over <see cref="TorrentInfo"/>. Lookups are case-insensitive.
///
/// Durations (<see cref="System.TimeSpan"/>) are exposed in whole seconds and
/// timestamps (<see cref="System.DateTimeOffset"/>) as unix seconds, so they can be
/// compared with the relational operators, e.g. <c>SeedingTime &gt; 86400</c>.
/// The enum <c>State</c> is matched by its name, e.g. <c>State == downloading</c>.
/// </summary>
public static class TorrentInfoProperties
{
    private static readonly Dictionary<string, TorrentProperty> Map =
        new(StringComparer.OrdinalIgnoreCase);

    static TorrentInfoProperties()
    {
        // ---- numbers (long / int / float) -------------------------------------
        Num("AmountLeft",        i => (double?)i.AmountLeft);
        Num("Availability",      i => i.Availability);
        Num("Completed",         i => (double?)i.Completed);
        Num("DlLimit",           i => (double?)i.DlLimit);
        Num("DownloadSpeed",     i => (double?)i.DownloadSpeed);
        Num("Downloaded",        i => (double?)i.Downloaded);
        Num("DownloadedSession", i => (double?)i.DownloadedSession);
        Num("MaxRatio",          i => i.MaxRatio);
        Num("NumComplete",       i => (double?)i.NumComplete);
        Num("NumIncomplete",     i => (double?)i.NumIncomplete);
        Num("NumLeechs",         i => (double?)i.NumLeechs);
        Num("NumSeeds",          i => (double?)i.NumSeeds);
        Num("Priority",          i => (double?)i.Priority);
        Num("Progress",          i => i.Progress);
        Num("Ratio",             i => i.Ratio);
        Num("RatioLimit",        i => i.RatioLimit);
        Num("Size",              i => (double?)i.Size);
        Num("TotalSize",         i => (double?)i.TotalSize);
        Num("UpLimit",           i => (double?)i.UpLimit);
        Num("Uploaded",          i => (double?)i.Uploaded);
        Num("UploadedSession",   i => (double?)i.UploadedSession);
        Num("UploadSpeed",       i => (double?)i.UploadSpeed);

        // ---- durations -> seconds --------------------------------------------
        Num("EstimatedTimeArrival", i => i.EstimatedTimeArrival?.TotalSeconds);
        Num("MaxSeedingTime",       i => i.MaxSeedingTime?.TotalSeconds);
        Num("ReannounceTime",       i => i.ReannounceTime?.TotalSeconds);
        Num("SeedingTime",          i => i.SeedingTime?.TotalSeconds);
        Num("SeedingTimeLimit",     i => i.SeedingTimeLimit?.TotalSeconds);
        Num("TimeActive",           i => i.TimeActive?.TotalSeconds);

        // ---- timestamps -> unix seconds --------------------------------------
        Num("AddedOn",      i => (double?)i.AddedOn?.ToUnixTimeSeconds());
        Num("CompletionOn", i => (double?)i.CompletionOn?.ToUnixTimeSeconds());
        Num("LastActivity", i => (double?)i.LastActivity?.ToUnixTimeSeconds());
        Num("SeenComplete", i => (double?)i.SeenComplete?.ToUnixTimeSeconds());

        // ---- strings ----------------------------------------------------------
        Str("Category",    i => i.Category);
        Str("ContentPath", i => i.ContentPath);
        Str("Hash",        i => i.Hash);
        Str("MagnetUri",   i => i.MagnetUri);
        Str("Name",        i => i.Name);
        Str("SavePath",    i => i.SavePath);
        Str("Tracker",     i => i.Tracker);
        Str("State",       i => i.State?.ToString());

        // ---- booleans ---------------------------------------------------------
        Bool("AutoTmmEnabled",                i => i.AutoTmmEnabled);
        Bool("FirstLastPiecePriorityEnabled", i => i.FirstLastPiecePriorityEnabled);
        Bool("ForceStartEnabled",             i => i.ForceStartEnabled);
        Bool("PrivateEnabled",                i => i.PrivateEnabled);
        Bool("SequentialDownloadEnabled",     i => i.SequentialDownloadEnabled);
        Bool("SuperSeedingEnabled",           i => i.SuperSeedingEnabled);

        // ---- collections ------------------------------------------------------
        Coll("TagList", i => i.TagList);
        Coll("Tags",    i => i.TagList); // friendly alias used in mapping files
    }

    /// <summary>Looks up a property descriptor by name, or <c>null</c> if unknown.</summary>
    public static TorrentProperty? Find(string name) =>
        Map.TryGetValue(name, out var prop) ? prop : null;

    /// <summary>All recognised property names (canonical casing).</summary>
    public static IReadOnlyCollection<string> Names => Map.Keys;

    private static void Num(string name, Func<TorrentInfo, double?> accessor) =>
        Map[name] = new TorrentProperty { Name = name, Kind = PropertyKind.Numeric, Number = accessor };

    private static void Str(string name, Func<TorrentInfo, string?> accessor) =>
        Map[name] = new TorrentProperty { Name = name, Kind = PropertyKind.Text, Text = accessor };

    private static void Bool(string name, Func<TorrentInfo, bool?> accessor) =>
        Map[name] = new TorrentProperty { Name = name, Kind = PropertyKind.Boolean, Boolean = accessor };

    private static void Coll(string name, Func<TorrentInfo, IEnumerable<string>?> accessor) =>
        Map[name] = new TorrentProperty { Name = name, Kind = PropertyKind.Collection, Collection = accessor };
}
