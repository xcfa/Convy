using Banned.Qbittorrent.Models.Torrent;

namespace Convy.PathExpressions.Expressions;

/// <summary>Logical conjunction (<c>&amp;&amp;</c>). Short-circuits on the left operand.</summary>
public sealed class AndExpression : IExpression
{
    private readonly IExpression _left;
    private readonly IExpression _right;

    public AndExpression(IExpression left, IExpression right)
    {
        _left = left;
        _right = right;
    }

    public bool Evaluate(TorrentInfo info) => _left.Evaluate(info) && _right.Evaluate(info);

    public override string ToString() => $"({_left} && {_right})";
}
