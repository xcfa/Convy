using Banned.Qbittorrent.Models.Torrent;

namespace Convy.PathExpressions.Expressions;

/// <summary>
/// Compares a numeric torrent property (e.g. <c>Size</c>, <c>Ratio</c>, <c>Uploaded</c>)
/// against a constant. All numeric kinds — <see cref="long"/>, <see cref="int"/>,
/// <see cref="float"/>, as well as durations/timestamps projected to seconds — are
/// normalised to <see cref="double"/> by the accessor so a single class covers them.
/// </summary>
public sealed class NumericCompareExpression : IExpression
{
    private readonly string _propertyName;
    private readonly Func<TorrentInfo, double?> _accessor;
    private readonly ComparisonOperator _op;
    private readonly double _value;

    public NumericCompareExpression(
        string propertyName,
        Func<TorrentInfo, double?> accessor,
        ComparisonOperator op,
        double value)
    {
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

        return _op.Apply(actual.Value.CompareTo(_value));
    }

    public override string ToString() => $"{_propertyName} {_op.ToSymbol()} {_value}";
}
