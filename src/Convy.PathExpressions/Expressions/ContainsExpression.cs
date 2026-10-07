using Convy.PathExpressions.Properties;

namespace Convy.PathExpressions.Expressions;

/// <summary>
/// Membership test against a collection-valued property — e.g. <c>Tags.Contains(Test)</c>.
/// Returns <c>true</c> when the collection contains an element equal (case-insensitively)
/// to the requested value.
/// </summary>
public sealed class ContainsExpression : IExpression
{
    private readonly string _propertyName;
    private readonly string _value;
    private readonly StringComparison _comparison;

    public ContainsExpression(
        string propertyName,
        string value,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        _propertyName = propertyName;
        _value = value;
        _comparison = comparison;
    }

    public bool Evaluate(IReadOnlyDictionary<string, object?> properties)
    {
        var items = PropertyValues.Collection(properties, _propertyName);
        if (items is null)
            return false;

        foreach (var item in items)
        {
            if (string.Equals(item, _value, _comparison))
                return true;
        }

        return false;
    }

    public void CollectProperties(ISet<string> names) => names.Add(_propertyName);

    public override string ToString() => $"{_propertyName}.Contains({_value})";
}
