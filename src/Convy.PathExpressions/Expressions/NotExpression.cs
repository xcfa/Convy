using Banned.Qbittorrent.Models.Torrent;

namespace Convy.PathExpressions.Expressions;

/// <summary>Logical negation (<c>!</c>).</summary>
public sealed class NotExpression : IExpression
{
    private readonly IExpression _operand;

    public NotExpression(IExpression operand) => _operand = operand;

    public bool Evaluate(TorrentInfo info) => !_operand.Evaluate(info);

    public override string ToString() => $"!{_operand}";
}
