namespace Convy.Infrastructure.Helpers
{
    /// <summary>
    /// Read-only filesystem queries used to validate placement targets. Abstracted so the
    /// placement and storage checks can be unit-tested without a real filesystem.
    /// </summary>
    public interface IFileSystemInspector
    {
        /// <summary>
        /// Returns the absolute path with every symbolic link in its existing part resolved
        /// (like <c>realpath</c>, but the path does not need to exist: the missing tail is
        /// appended to the resolved existing ancestor).
        /// </summary>
        string GetRealPath(string path);

        /// <summary>
        /// Identifies the mount that contains <paramref name="path"/> (or its nearest existing
        /// ancestor). Hard links only work within one mount, so two paths with different
        /// identifiers cannot be linked. Returns <c>null</c> when the platform cannot tell.
        /// </summary>
        Task<string?> GetMountIdAsync(string path, CancellationToken cancellationToken);

        /// <summary>
        /// Free bytes available to the process on the filesystem holding <paramref name="path"/>
        /// (or its nearest existing ancestor), or <c>null</c> when it cannot be determined.
        /// </summary>
        long? GetAvailableFreeSpace(string path);

        /// <summary>Whether a directory exists at <paramref name="path"/>.</summary>
        bool DirectoryExists(string path);
    }
}
