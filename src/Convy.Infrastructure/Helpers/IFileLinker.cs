namespace Convy.Infrastructure.Helpers
{
    /// <summary>
    /// Filesystem operations used when routing torrent files. Abstracted so the linking
    /// logic can be unit-tested without touching a real filesystem.
    /// </summary>
    public interface IFileLinker
    {
        /// <summary>Whether a file exists at the given path.</summary>
        bool Exists(string path);

        /// <summary>
        /// Hard-links <paramref name="source"/> to <paramref name="destination"/>,
        /// creating the destination's parent directories first. Throws on failure
        /// (e.g. cross-filesystem link).
        /// </summary>
        void Link(string source, string destination);
    }
}
