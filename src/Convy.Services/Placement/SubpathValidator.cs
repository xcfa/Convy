using System.Text;

namespace Convy.Services.Placement;

/// <summary>
/// Validates the sub-path the agent may pass to place a download under. An invalid value is
/// rejected with a reason, never silently corrected.
/// </summary>
public static class SubpathValidator
{
    private const int MaxSegmentBytes = 255;
    private static readonly char[] ForbiddenChars = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary>
    /// Checks <paramref name="subpath"/>: relative, '/'-separated, no <c>..</c>/<c>.</c>/empty
    /// segments, no control characters or <c>&lt;&gt;:"|?*</c>, at most 255 UTF-8 bytes per
    /// segment. A <c>null</c> or empty value means "no sub-path" and is valid.
    /// </summary>
    /// <param name="subpath">The requested sub-path.</param>
    /// <param name="normalized">The validated sub-path, or <c>null</c> when none was given.</param>
    /// <param name="error">Why the sub-path was rejected.</param>
    public static bool TryValidate(string? subpath, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrEmpty(subpath))
        {
            return true;
        }

        if (subpath[0] is '/' or '\\')
        {
            error = "subpath must be relative (no leading '/').";
            return false;
        }

        if (subpath.Contains('\\'))
        {
            error = "subpath must use '/' as the directory separator.";
            return false;
        }

        if (subpath.Any(char.IsControl))
        {
            error = "subpath must not contain control characters.";
            return false;
        }

        if (subpath.IndexOfAny(ForbiddenChars) >= 0)
        {
            error = "subpath must not contain any of the characters < > : \" | ? *.";
            return false;
        }

        var segments = subpath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                error = "subpath must not contain empty segments ('//' or a trailing '/').";
                return false;
            }

            if (segment is "." or "..")
            {
                error = "subpath must not contain '.' or '..' segments.";
                return false;
            }

            if (segment.Trim().Length != segment.Length)
            {
                error = $"subpath segment '{segment}' must not start or end with whitespace.";
                return false;
            }

            if (Encoding.UTF8.GetByteCount(segment) > MaxSegmentBytes)
            {
                error = $"subpath segment '{segment[..Math.Min(segment.Length, 32)]}…' is longer than {MaxSegmentBytes} bytes.";
                return false;
            }
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> lies strictly inside <paramref name="baseDirectory"/>.
    /// Both paths must already be absolute and normalised (symbolic links resolved).
    /// </summary>
    public static bool IsInside(string baseDirectory, string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        return full.Length > root.Length
               && full.StartsWith(root, StringComparison.Ordinal)
               && (full[root.Length] == Path.DirectorySeparatorChar || full[root.Length] == Path.AltDirectorySeparatorChar);
    }
}
