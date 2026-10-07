using System.Text;

namespace Convy.Infrastructure.Helpers
{
    /// <summary>
    /// Real <see cref="IFileSystemInspector"/>. Mount identification reads
    /// <c>/proc/self/mountinfo</c> and is therefore Linux-only; elsewhere it returns <c>null</c>.
    /// </summary>
    public sealed class FileSystemInspector : IFileSystemInspector
    {
        private const string MountInfoPath = "/proc/self/mountinfo";

        public string GetRealPath(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var segments = full[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                var next = Path.Combine(current, segments[i]);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);

                if (!info.Exists)
                {
                    // The rest does not exist yet, so it cannot contain links.
                    return Path.Combine([current, .. segments[i..]]);
                }

                current = info.LinkTarget is not null
                    ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next
                    : next;
            }

            return current;
        }

        public async Task<string?> GetMountIdAsync(string path, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(MountInfoPath))
            {
                return null;
            }

            var realPath = GetRealPath(path);
            var lines = await File.ReadAllLinesAsync(MountInfoPath, cancellationToken).ConfigureAwait(false);

            string? bestId = null;
            var bestLength = -1;

            foreach (var line in lines)
            {
                // "<id> <parent> <major:minor> <root> <mount point> <options> ..."
                var fields = line.Split(' ');
                if (fields.Length < 5)
                {
                    continue;
                }

                var mountPoint = Unescape(fields[4]);
                if (IsUnder(realPath, mountPoint) && mountPoint.Length > bestLength)
                {
                    bestId = fields[0];
                    bestLength = mountPoint.Length;
                }
            }

            return bestId;
        }

        public long? GetAvailableFreeSpace(string path)
        {
            var existing = NearestExistingDirectory(path);
            if (existing is null)
            {
                return null;
            }

            try
            {
                return new DriveInfo(existing).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        public bool DirectoryExists(string path) => Directory.Exists(path);

        private static string? NearestExistingDirectory(string path)
        {
            var current = Path.GetFullPath(path);
            while (!Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                {
                    return null;
                }

                current = parent;
            }

            return current;
        }

        private static bool IsUnder(string path, string mountPoint)
        {
            if (mountPoint == "/")
            {
                return true;
            }

            return path == mountPoint
                   || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
        }

        /// <summary>Decodes the octal escapes (<c>\040</c> for a space, …) used in mountinfo.</summary>
        private static string Unescape(string value)
        {
            if (!value.Contains('\\'))
            {
                return value;
            }

            var bytes = new List<byte>(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 3 < value.Length
                    && IsOctal(value[i + 1]) && IsOctal(value[i + 2]) && IsOctal(value[i + 3]))
                {
                    bytes.Add((byte)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                    i += 3;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
                }
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static bool IsOctal(char c) => c is >= '0' and <= '7';
    }
}
