using Convy.PathExpressions.Properties;

namespace Convy.PathExpressions.Expressions;

/// <summary>
/// Compares a string-valued property (e.g. <c>Category</c>, <c>Name</c>, <c>State</c>) for
/// (in)equality. Comparison is case-insensitive by default, which suits
/// category/tag/state matching from a config file.
/// </summary>
public sealed class StringCompareExpression : IExpression
{
    private readonly string _propertyName;
    private readonly ComparisonOperator _op;
    private readonly string _value;
    private readonly StringComparison _comparison;

    public StringCompareExpression(
        string propertyName,
        ComparisonOperator op,
        string value,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        if (!op.IsEquality())
            throw new ArgumentException(
                $"Operator '{op.ToSymbol()}' is not valid for the string property '{propertyName}'.",
                nameof(op));

        _propertyName = propertyName;
        _op = op;
        _value = value;
        _comparison = comparison;
    }

    public bool Evaluate(IReadOnlyDictionary<string, object?> properties)
    {
        var actual = PropertyValues.Text(properties, _propertyName);
        if (actual is null)
            return false;

        var equal = string.Equals(actual, _value, _comparison);
        return _op == ComparisonOperator.Equal ? equal : !equal;
    }

    public void CollectProperties(ISet<string> names) => names.Add(_propertyName);

    public override string ToString() => $"{_propertyName} {_op.ToSymbol()} {_value}";
}
