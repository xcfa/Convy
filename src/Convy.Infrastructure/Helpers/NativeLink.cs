using System.IO;
using System.Runtime.InteropServices;

namespace Convy.Infrastructure.Helpers
{
    /// <summary>
    /// Thin P/Invoke wrapper over the POSIX <c>link(2)</c> syscall for creating
    /// hard links. Used to place a torrent's already-downloaded files into the
    /// routed output directory without copying their content.
    /// </summary>
    public static partial class NativeLink
    {
        // SetLastError is required so Marshal.GetLastPInvokeError() returns the real
        // errno; without it the value is meaningless (often 0).
        [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int Link(string existing, string @new);

        public static void CreateHardLink(string source, string dest)
        {
            if (Link(source, dest) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                var detail = errno switch
                {
                    18 => " (EXDEV: source and destination are on different filesystems; hard links cannot cross filesystems)",
                    17 => " (EEXIST: destination already exists)",
                    13 => " (EACCES: permission denied)",
                    2 => " (ENOENT: source path does not exist)",
                    _ => string.Empty,
                };

                throw new IOException($"link() failed with errno {errno}{detail} for '{source}' -> '{dest}'");
            }
        }
    }
}
