using Convy.PathExpressions.Properties;

namespace Convy.PathExpressions.Expressions;

/// <summary>
/// Compares a boolean property (e.g. <c>AutoTmmEnabled</c>,
/// <c>SequentialDownloadEnabled</c>) against <c>true</c>/<c>false</c>.
/// </summary>
public sealed class BoolCompareExpression : IExpression
{
    private readonly string _propertyName;
    private readonly ComparisonOperator _op;
    private readonly bool _value;

    public BoolCompareExpression(
        string propertyName,
        ComparisonOperator op,
        bool value)
    {
        if (!op.IsEquality())
            throw new ArgumentException(
                $"Operator '{op.ToSymbol()}' is not valid for the boolean property '{propertyName}'.",
                nameof(op));

        _propertyName = propertyName;
        _op = op;
        _value = value;
    }

    public bool Evaluate(IReadOnlyDictionary<string, object?> properties)
    {
        var actual = PropertyValues.Boolean(properties, _propertyName);
        if (actual is null)
            return false;

        var equal = actual.Value == _value;
        return _op == ComparisonOperator.Equal ? equal : !equal;
    }

    public void CollectProperties(ISet<string> names) => names.Add(_propertyName);

    public override string ToString() => $"{_propertyName} {_op.ToSymbol()} {_value.ToString().ToLowerInvariant()}";
}
