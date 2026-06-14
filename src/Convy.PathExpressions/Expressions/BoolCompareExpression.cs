using Banned.Qbittorrent.Models.Torrent;

namespace Convy.PathExpressions.Expressions;

/// <summary>
/// Compares a boolean torrent property (e.g. <c>AutoTmmEnabled</c>,
/// <c>SequentialDownloadEnabled</c>) against <c>true</c>/<c>false</c>.
/// </summary>
public sealed class BoolCompareExpression : IExpression
{
    private readonly string _propertyName;
    private readonly Func<TorrentInfo, bool?> _accessor;
    private readonly ComparisonOperator _op;
    private readonly bool _value;

    public BoolCompareExpression(
        string propertyName,
        Func<TorrentInfo, bool?> accessor,
        ComparisonOperator op,
        bool value)
    {
        if (!op.IsEquality())
            throw new ArgumentException(
                $"Operator '{op.ToSymbol()}' is not valid for the boolean property '{propertyName}'.",
                nameof(op));

        _propertyName = propertyName;
        _accessor = accessor;
        _op = op;
        _value = value;
    }

    public bool Evaluate(TorrentInfo info)
    {
        var actual = _accessor(info);
        if (actual is null)
            return false;

        var equal = actual.Value == _value;
        return _op == ComparisonOperator.Equal ? equal : !equal;
    }

    public override string ToString() => $"{_propertyName} {_op.ToSymbol()} {_value.ToString().ToLowerInvariant()}";
}
