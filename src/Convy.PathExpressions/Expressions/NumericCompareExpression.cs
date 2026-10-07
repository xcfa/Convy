using Convy.PathExpressions.Properties;

namespace Convy.PathExpressions.Expressions;

/// <summary>
/// Compares a numeric property (e.g. <c>Size</c>, <c>Ratio</c>, <c>Uploaded</c>) against a
/// constant. All numeric kinds — integers, floats, as well as durations/timestamps
/// projected to seconds — are normalised to <see cref="double"/> so a single class covers them.
/// </summary>
public sealed class NumericCompareExpression : IExpression
{
    private readonly string _propertyName;
    private readonly ComparisonOperator _op;
    private readonly double _value;

    public NumericCompareExpression(
        string propertyName,
        ComparisonOperator op,
        double value)
    {
        _propertyName = propertyName;
        _op = op;
        _value = value;
    }

    public bool Evaluate(IReadOnlyDictionary<string, object?> properties)
    {
        var actual = PropertyValues.Number(properties, _propertyName);
        if (actual is null)
            return false;

        return _op.Apply(actual.Value.CompareTo(_value));
    }

    public void CollectProperties(ISet<string> names) => names.Add(_propertyName);

    public override string ToString() => $"{_propertyName} {_op.ToSymbol()} {_value}";
}
