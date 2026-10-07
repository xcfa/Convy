using Convy.PathExpressions.Mappings;
using Convy.Services.Downloads;

namespace Convy.Services.Placement;

/// <summary>Where a download goes and how its paths are mapped.</summary>
/// <param name="RuleName">Name of the matching rule, or <c>null</c>.</param>
/// <param name="Directory">Target directory, or <c>null</c> when the files stay where they are.</param>
/// <param name="BaseDirectory">The directory the target must stay inside (rule path or save path).</param>
/// <param name="ReplaceRoot">Whether the download's root folder is replaced by the sub-path.</param>
public sealed record PlacementTarget(string? RuleName, string? Directory, string? BaseDirectory, bool ReplaceRoot)
{
    /// <summary>No rule and no sub-path: files are left in the download directory.</summary>
    public bool LeaveInPlace => Directory is null;
}

/// <summary>One hard link to create.</summary>
/// <param name="Source">Source path relative to the item's save path ('/'-separated).</param>
/// <param name="Destination">Absolute destination path.</param>
public sealed record PlannedLink(string Source, string Destination);

/// <summary>
/// Pure placement decisions (spec section 8.3):
/// <list type="table">
/// <item><term>rule + sub-path</term><description><c>rule path / sub-path</c>; the sub-path replaces the root folder.</description></item>
/// <item><term>rule, no sub-path</term><description><c>rule path</c> with the original structure (classic Convy).</description></item>
/// <item><term>no rule + sub-path</term><description><c>save path / sub-path</c>, inside the download directory.</description></item>
/// <item><term>neither</term><description>files stay in place.</description></item>
/// </list>
/// </summary>
public static class PlacementPlanner
{
    public static PlacementTarget ResolveTarget(MappingRule? rule, string? subpath, string savePath)
    {
        if (rule is not null)
        {
            return subpath is null
                ? new PlacementTarget(rule.Name, rule.OutputPath, rule.OutputPath, ReplaceRoot: false)
                : new PlacementTarget(rule.Name, Path.Combine(rule.OutputPath, subpath), rule.OutputPath, ReplaceRoot: true);
        }

        return subpath is null
            ? new PlacementTarget(null, null, null, ReplaceRoot: false)
            : new PlacementTarget(null, Path.Combine(savePath, subpath), savePath, ReplaceRoot: true);
    }

    /// <summary>
    /// The download's root folder: the first path segment when every file lives under the
    /// same one (a multi-file torrent, a Soulseek folder). <c>null</c> for single-file
    /// downloads and layouts without a common root.
    /// </summary>
    public static string? FindRoot(IEnumerable<DownloadFile> allFiles)
    {
        string? root = null;
        var any = false;

        foreach (var file in allFiles)
        {
            any = true;
            var slash = file.Path.IndexOf('/');
            if (slash <= 0)
            {
                return null;
            }

            var first = file.Path[..slash];
            if (root is null)
            {
                root = first;
            }
            else if (!string.Equals(root, first, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return any ? root : null;
    }

    /// <summary>
    /// Maps files into <paramref name="targetDirectory"/>. With <paramref name="stripRoot"/>
    /// set, that leading folder is removed, e.g. <c>Movie.2019.WEB-DL/movie.mkv</c> with
    /// sub-path <c>Movie (2019)</c> becomes <c>&lt;rule path&gt;/Movie (2019)/movie.mkv</c>.
    /// </summary>
    public static IReadOnlyList<PlannedLink> PlanLinks(IEnumerable<string> sourcePaths, string targetDirectory, string? stripRoot)
    {
        return sourcePaths
            .Select(source => new PlannedLink(source, Path.Combine(targetDirectory, MapPath(source, stripRoot))))
            .ToList();
    }

    /// <summary>
    /// The path of <paramref name="source"/> relative to the target directory: unchanged, or
    /// without the leading <paramref name="stripRoot"/> folder.
    /// </summary>
    public static string MapPath(string source, string? stripRoot) =>
        stripRoot is not null && source.StartsWith(stripRoot + "/", StringComparison.Ordinal)
            ? source[(stripRoot.Length + 1)..]
            : source;

    /// <summary>
    /// Whether an item file belongs to the job's selection. Selected paths are relative to the
    /// result root; item paths may include the root folder, so both spellings match.
    /// </summary>
    public static bool IsInSelection(string itemPath, IReadOnlySet<string>? selection)
    {
        if (selection is null || selection.Contains(itemPath))
        {
            return true;
        }

        var slash = itemPath.IndexOf('/');
        return slash > 0 && selection.Contains(itemPath[(slash + 1)..]);
    }
}
