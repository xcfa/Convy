using Banned.Qbittorrent.Models.Torrent;

namespace Convy.PathExpressions.Expressions;

/// <summary>
/// Membership test against a collection-valued property — e.g. <c>Tags.Contains(Test)</c>.
/// Returns <c>true</c> when the collection contains an element equal (case-insensitively)
/// to the requested value.
/// </summary>
public sealed class ContainsExpression : IExpression
{
    private readonly string _propertyName;
    private readonly Func<TorrentInfo, IEnumerable<string>?> _accessor;
    private readonly string _value;
    private readonly StringComparison _comparison;

    public ContainsExpression(
        string propertyName,
        Func<TorrentInfo, IEnumerable<string>?> accessor,
        string value,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        _propertyName = propertyName;
        _accessor = accessor;
        _value = value;
        _comparison = comparison;
    }

    public bool Evaluate(TorrentInfo info)
    {
        var items = _accessor(info);
        if (items is null)
            return false;

        foreach (var item in items)
        {
            if (string.Equals(item, _value, _comparison))
                return true;
        }

        return false;
    }

    public override string ToString() => $"{_propertyName}.Contains({_value})";
}
