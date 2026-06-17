using System.IO;

namespace Convy.Infrastructure.Helpers
{
    /// <summary>Real <see cref="IFileLinker"/> backed by the filesystem and
    /// <see cref="NativeLink"/>.</summary>
    public sealed class FileLinker : IFileLinker
    {
        public bool Exists(string path) => File.Exists(path);

        public void Link(string source, string destination)
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            NativeLink.CreateHardLink(source, destination);
        }
    }
}
