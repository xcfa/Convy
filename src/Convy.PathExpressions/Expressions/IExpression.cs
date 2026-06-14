using Banned.Qbittorrent.Models.Torrent;

namespace Convy.PathExpressions.Expressions;

/// <summary>
/// A node in a compiled filter expression tree. Every node knows how to decide,
/// for a concrete <see cref="TorrentInfo"/>, whether it matches.
/// </summary>
public interface IExpression
{
    /// <summary>
    /// Evaluates this node against the given torrent.
    /// </summary>
    /// <remarks>
    /// A missing/<c>null</c> property is treated as "does not match" rather than
    /// throwing, so an incomplete <see cref="TorrentInfo"/> simply fails the rule.
    /// </remarks>
    bool Evaluate(TorrentInfo info);
}
