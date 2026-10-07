using Banned.Qbittorrent.Models.Torrent;

namespace Convy.Services.Downloaders.QBittorrent;

/// <summary>
/// Projects a qBittorrent <see cref="TorrentInfo"/> onto the routing-rule property bag.
/// Every property qBittorrent knows is published, even when unset (<c>null</c>), so rules
/// keep their "unset never matches" semantics for torrents. Names and kinds must agree
/// with <see cref="PathExpressions.Properties.RuleProperties"/>.
///
/// Durations are published in whole seconds and timestamps as unix seconds; the enum
/// <c>State</c> is published by name.
/// </summary>
public static class TorrentInfoProperties
{
    private static readonly (string Name, Func<TorrentInfo, object?> Accessor)[] Accessors =
    [
        // ---- numbers (long / int / float) -------------------------------------
        ("AmountLeft",        i => (double?)i.AmountLeft),
        ("Availability",      i => (double?)i.Availability),
        ("Completed",         i => (double?)i.Completed),
        ("DlLimit",           i => (double?)i.DlLimit),
        ("DownloadSpeed",     i => (double?)i.DownloadSpeed),
        ("Downloaded",        i => (double?)i.Downloaded),
        ("DownloadedSession", i => (double?)i.DownloadedSession),
        ("MaxRatio",          i => (double?)i.MaxRatio),
        ("NumComplete",       i => (double?)i.NumComplete),
        ("NumIncomplete",     i => (double?)i.NumIncomplete),
        ("NumLeechs",         i => (double?)i.NumLeechs),
        ("NumSeeds",          i => (double?)i.NumSeeds),
        ("Priority",          i => (double?)i.Priority),
        ("Progress",          i => (double?)i.Progress),
        ("Ratio",             i => (double?)i.Ratio),
        ("RatioLimit",        i => (double?)i.RatioLimit),
        ("Size",              i => (double?)i.Size),
        ("TotalSize",         i => (double?)i.TotalSize),
        ("UpLimit",           i => (double?)i.UpLimit),
        ("Uploaded",          i => (double?)i.Uploaded),
        ("UploadedSession",   i => (double?)i.UploadedSession),
        ("UploadSpeed",       i => (double?)i.UploadSpeed),

        // ---- durations -> seconds --------------------------------------------
        ("EstimatedTimeArrival", i => i.EstimatedTimeArrival?.TotalSeconds),
        ("MaxSeedingTime",       i => i.MaxSeedingTime?.TotalSeconds),
        ("ReannounceTime",       i => i.ReannounceTime?.TotalSeconds),
        ("SeedingTime",          i => i.SeedingTime?.TotalSeconds),
        ("SeedingTimeLimit",     i => i.SeedingTimeLimit?.TotalSeconds),
        ("TimeActive",           i => i.TimeActive?.TotalSeconds),

        // ---- timestamps -> unix seconds --------------------------------------
        ("AddedOn",      i => (double?)i.AddedOn?.ToUnixTimeSeconds()),
        ("CompletionOn", i => (double?)i.CompletionOn?.ToUnixTimeSeconds()),
        ("LastActivity", i => (double?)i.LastActivity?.ToUnixTimeSeconds()),
        ("SeenComplete", i => (double?)i.SeenComplete?.ToUnixTimeSeconds()),

        // ---- strings ----------------------------------------------------------
        ("Category",    i => i.Category),
        ("ContentPath", i => i.ContentPath),
        ("Hash",        i => i.Hash),
        ("MagnetUri",   i => i.MagnetUri),
        ("Name",        i => i.Name),
        ("SavePath",    i => i.SavePath),
        ("Tracker",     i => i.Tracker),
        ("State",       i => i.State?.ToString()),

        // ---- booleans ---------------------------------------------------------
        ("AutoTmmEnabled",                i => i.AutoTmmEnabled),
        ("FirstLastPiecePriorityEnabled", i => i.FirstLastPiecePriorityEnabled),
        ("ForceStartEnabled",             i => i.ForceStartEnabled),
        ("PrivateEnabled",                i => i.PrivateEnabled),
        ("SequentialDownloadEnabled",     i => i.SequentialDownloadEnabled),
        ("SuperSeedingEnabled",           i => i.SuperSeedingEnabled),

        // ---- collections ------------------------------------------------------
        ("TagList", i => i.TagList),
        ("Tags",    i => i.TagList), // friendly alias used in mapping files
    ];

    /// <summary>The property names published for every torrent.</summary>
    public static IEnumerable<string> Names => Accessors.Select(a => a.Name);

    /// <summary>Builds the rule property bag for a torrent.</summary>
    public static Dictionary<string, object?> ToProperties(TorrentInfo info)
    {
        var properties = new Dictionary<string, object?>(Accessors.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, accessor) in Accessors)
        {
            properties[name] = accessor(info);
        }

        return properties;
    }
}
