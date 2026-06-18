using Microsoft.Extensions.Configuration;

namespace Convy.Configuration;

/// <summary>
/// Configuration source backed by the SQLite <c>UserSettings</c> table.
/// Keeps a reference to the created provider so callers can trigger
/// <see cref="DbConfigurationProvider.Reload"/> after a write.
/// </summary>
public sealed class DbConfigurationSource : IConfigurationSource
{
    private readonly string _connectionString;

    public DbConfigurationSource(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// The provider instance created by <see cref="Build"/>. Available
    /// after the configuration root is built.
    /// </summary>
    public DbConfigurationProvider? Provider { get; private set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        Provider = new DbConfigurationProvider(_connectionString);
        return Provider;
    }
}
