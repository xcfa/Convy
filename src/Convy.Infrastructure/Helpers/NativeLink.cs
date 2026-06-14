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
        [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int Link(string existing, string @new);

        public static void CreateHardLink(string source, string dest)
        {
            if (Link(source, dest) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw new IOException($"link() failed with errno {errno} for '{source}' -> '{dest}'");
            }
        }
    }
}
