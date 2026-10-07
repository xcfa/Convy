using System.Text.RegularExpressions;

namespace Convy.Sources.Slskd;

/// <summary>
/// Soulseek path rules shared by the Soulseek source and the slskd downloader. Remote paths
/// use backslashes (<c>@@abcde\Music\Artist\Album\01.flac</c>); slskd saves a download as
/// <c>&lt;downloads&gt;/&lt;last remote folder&gt;/&lt;file name&gt;</c> (its default
/// <c>${SOURCE_DIRECTORY}</c> destination).
/// </summary>
public static partial class SoulseekPaths
{
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

    /// <summary>
    /// The local folder slskd creates for a remote directory: its last segment, sanitised;
    /// empty for files directly in a share root (<c>@@xxxxx</c>).
    /// </summary>
    public static string LocalFolderOf(string directory)
    {
        var last = directory[(directory.LastIndexOf('\\') + 1)..];
        return ShareRoot().IsMatch(last) ? string.Empty : Sanitize(last);
    }

    /// <summary>Where slskd stores a downloaded remote file, relative to its downloads directory.</summary>
    public static string LocalPathOf(string filename)
    {
        var folder = LocalFolderOf(DirectoryOf(filename));
        var name = Sanitize(FileNameOf(filename));
        return folder.Length == 0 ? name : folder + "/" + name;
    }

    /// <summary>Item reference of a user's folder: <c>&lt;user&gt;/&lt;directory&gt;</c>, both URI-escaped.</summary>
    public static string ItemRef(string username, string directory) =>
        Uri.EscapeDataString(username) + "/" + Uri.EscapeDataString(directory);

    /// <summary>Splits an <see cref="ItemRef"/>; returns <c>false</c> for anything else.</summary>
    public static bool TryParseItemRef(string itemRef, out string username, out string directory)
    {
        var slash = itemRef.IndexOf('/');
        if (slash <= 0)
        {
            username = directory = string.Empty;
            return false;
        }

        username = Uri.UnescapeDataString(itemRef[..slash]);
        directory = Uri.UnescapeDataString(itemRef[(slash + 1)..]);
        return true;
    }

    /// <summary>On Linux slskd only replaces the characters a file name cannot contain.</summary>
    private static string Sanitize(string segment) => segment.Replace('/', '_').Replace('\0', '_');

    [GeneratedRegex("^@@[a-zA-Z0-9]{5,}$")]
    private static partial Regex ShareRoot();
}
