namespace Convy.Services.Rules
{
    /// <summary>
    /// Supplies the current routing rules, reloading them when the underlying file
    /// changes. Implementations must be safe to call from the sync loop.
    /// </summary>
    public interface IRulesProvider
    {
        /// <summary>
        /// Returns the current rules snapshot, reloading from disk first if the file has
        /// changed since the last call. The returned snapshot is immutable; capture it
        /// once per cycle to get a consistent view even if a reload happens concurrently.
        /// </summary>
        RulesSnapshot GetCurrent();
    }
}
