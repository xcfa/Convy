using Banned.Qbittorrent.Models.Torrent;
using Convy.PathExpressions.Expressions;

namespace Convy.PathExpressions.Mappings;

/// <summary>
/// One line of ConvyMappings.ini: a compiled <see cref="Condition"/> plus the
/// <see cref="OutputPath"/> a torrent is routed to when the condition holds.
/// </summary>
public sealed class MappingRule
{
    public required IExpression Condition { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>The original condition text, kept for logging / diagnostics.</summary>
    public required string RawCondition { get; init; }

    /// <summary>1-based line number in the source file.</summary>
    public required int LineNumber { get; init; }

    public bool Matches(TorrentInfo info) => Condition.Evaluate(info);
}
