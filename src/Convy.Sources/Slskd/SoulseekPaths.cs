using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Convy.Sources.Slskd;

/// <summary>
/// Soulseek path rules shared by the Soulseek source and the slskd downloader. Remote paths
/// use backslashes (<c>@@abcde\Music\Artist\Album\01.flac</c>).
/// </summary>
/// <remarks>
/// Downloads started by Convy are queued as slskd batches with an explicit destination,
/// <c>convy/&lt;key&gt;/&lt;folder&gt;[/&lt;subfolder&gt;]</c> under slskd's downloads
/// directory, where the key identifies the user's folder. That keeps the folder structure
/// (<c>CD2</c>, <c>Scans</c>) and avoids clashes between folders of the same name. Downloads
/// started in slskd itself follow its default destination,
/// <c>&lt;downloads&gt;/&lt;last remote folder&gt;/&lt;file name&gt;</c>.
/// </remarks>
public static partial class SoulseekPaths
{
    /// <summary>Top folder of Convy's downloads inside slskd's downloads directory.</summary>
    public const string ConvyFolder = "convy";

    private const string ManualPrefix = "manual/";

    /// <summary>The remote directory of a remote file name.</summary>
    public static string DirectoryOf(string filename)
    {
        var slash = filename.LastIndexOf('\\');
        return slash < 0 ? string.Empty : filename[..slash];
    }

    /// <summary>The bare file name of a remote file name.</summary>
    public static string FileNameOf(string filename) => filename[(filename.LastIndexOf('\\') + 1)..];

    /// <summary>
    /// A remote file relative to <paramref name="directory"/>, '/'-separated (the path the
    /// agent sees in <c>list_files</c> and uses in include/exclude).
    /// </summary>
    public static string RelativeTo(string directory, string filename)
    {
        var relative = directory.Length > 0 && filename.StartsWith(directory + "\\", StringComparison.Ordinal)
            ? filename[(directory.Length + 1)..]
            : FileNameOf(filename);
        return relative.Replace('\\', '/');
    }

    /// <summary>The full remote name of a file given relative to <paramref name="directory"/>.</summary>
    public static string Combine(string directory, string relativePath) =>
        directory.Length == 0 ? relativePath.Replace('/', '\\') : directory + "\\" + relativePath.Replace('/', '\\');

    /// <summary>Whether <paramref name="directory"/> is <paramref name="root"/> or one of its subfolders.</summary>
    public static bool IsUnder(string directory, string root) =>
        directory == root || directory.StartsWith(root + "\\", StringComparison.Ordinal);

    /// <summary>
    /// The local folder slskd creates for a remote directory by default: its last segment,
    /// sanitised; empty for files directly in a share root (<c>@@xxxxx</c>).
    /// </summary>
    public static string LocalFolderOf(string directory)
    {
        var last = directory[(directory.LastIndexOf('\\') + 1)..];
        return ShareRoot().IsMatch(last) ? string.Empty : Sanitize(last);
    }

    /// <summary>Where slskd stores a remote file by default, relative to its downloads directory.</summary>
    public static string LocalPathOf(string filename)
    {
        var folder = LocalFolderOf(DirectoryOf(filename));
        var name = Sanitize(FileNameOf(filename));
        return folder.Length == 0 ? name : folder + "/" + name;
    }

    /// <summary>
    /// Folders of a multi-disc release (<c>CD1</c>, <c>Disc 2</c>) belong to the release
    /// folder above them.
    /// </summary>
    public static bool IsDiscFolder(string directory) =>
        DiscFolder().IsMatch(directory[(directory.LastIndexOf('\\') + 1)..]);

    /// <summary>Key of a user's folder in Convy's download destinations.</summary>
    public static string FolderKey(string username, string directory) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(username + "\n" + directory)))[..12];

    /// <summary>
    /// The folder (relative to <c>convy/&lt;key&gt;</c>) a download of <paramref name="directory"/>
    /// is stored under: the folder's own name, or <c>files</c> for a share root.
    /// </summary>
    public static string ReleaseFolderOf(string directory) =>
        LocalFolderOf(directory) is { Length: > 0 } name && name is not "." and not ".." ? name : "files";

    /// <summary>
    /// The batch destination for the files of <paramref name="relativeSubfolder"/> ('/'-separated,
    /// empty for the folder itself) of a user's folder.
    /// </summary>
    public static string DestinationOf(string username, string directory, string relativeSubfolder)
    {
        var destination = $"{ConvyFolder}/{FolderKey(username, directory)}/{ReleaseFolderOf(directory)}";
        return relativeSubfolder.Length == 0
            ? destination
            : destination + "/" + string.Join('/', relativeSubfolder.Split('/').Select(SafeSegment));
    }

    /// <summary>
    /// Recognises a destination made by <see cref="DestinationOf"/> for a transfer and recovers
    /// the user's folder it belongs to and the path of the file inside the item.
    /// </summary>
    /// <param name="destination">The batch destination.</param>
    /// <param name="username">The transfer's user.</param>
    /// <param name="filename">The transfer's remote file name.</param>
    /// <param name="directory">The user's folder (the item root).</param>
    /// <param name="itemPath">Path of the file relative to <c>convy/&lt;key&gt;</c>.</param>
    public static bool TryParseDestination(
        string? destination, string username, string filename, out string directory, out string itemPath)
    {
        directory = itemPath = string.Empty;
        var parts = destination?.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 3 } || parts[0] != ConvyFolder)
        {
            return false;
        }

        // convy/<key>/<release>/<sub>/<sub>…: the remote directory has as many trailing
        // segments below the user's folder as there are subfolders.
        var subfolders = parts.Length - 3;
        var segments = DirectoryOf(filename).Split('\\');
        if (segments.Length <= subfolders)
        {
            return false;
        }

        var root = string.Join('\\', segments[..^subfolders]);
        if (FolderKey(username, root) != parts[1])
        {
            return false;
        }

        directory = root;
        itemPath = string.Join('/', parts[2..]) + "/" + Sanitize(FileNameOf(filename));
        return true;
    }

    /// <summary>
    /// Item reference of a user's folder downloaded by Convy: <c>&lt;user&gt;/&lt;directory&gt;</c>,
    /// both URI-escaped.
    /// </summary>
    public static string ItemRef(string username, string directory) =>
        Uri.EscapeDataString(username) + "/" + Uri.EscapeDataString(directory);

    /// <summary>Item reference of a folder downloaded in slskd itself (default layout).</summary>
    public static string ManualItemRef(string username, string directory) => ManualPrefix + ItemRef(username, directory);

    /// <summary>Splits an item reference; returns <c>false</c> for anything else.</summary>
    public static bool TryParseItemRef(string itemRef, out string username, out string directory, out bool manual)
    {
        manual = itemRef.StartsWith(ManualPrefix, StringComparison.Ordinal) && itemRef.Count(c => c == '/') == 2;
        var value = manual ? itemRef[ManualPrefix.Length..] : itemRef;

        var slash = value.IndexOf('/');
        if (slash <= 0 || value.IndexOf('/', slash + 1) >= 0)
        {
            username = directory = string.Empty;
            return false;
        }

        username = Uri.UnescapeDataString(value[..slash]);
        directory = Uri.UnescapeDataString(value[(slash + 1)..]);
        return true;
    }

    /// <summary>On Linux slskd only replaces the characters a file name cannot contain.</summary>
    private static string Sanitize(string segment) => segment.Replace('/', '_').Replace('\0', '_');

    /// <summary>A destination segment that cannot climb out of the destination.</summary>
    private static string SafeSegment(string segment) =>
        segment is "" or "." or ".." ? "_" : Sanitize(segment);

    [GeneratedRegex("^@@[a-zA-Z0-9]{5,}$")]
    private static partial Regex ShareRoot();

    [GeneratedRegex(@"^(cd|disc|disk)[\s._-]*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolder();
}
