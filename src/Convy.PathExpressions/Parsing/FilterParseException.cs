namespace Convy.PathExpressions.Parsing;

/// <summary>
/// Thrown when a filter string is syntactically invalid or refers to an unknown
/// property / illegal operator combination.
/// </summary>
public sealed class FilterParseException : Exception
{
    public FilterParseException(string message) : base(message) { }

    public FilterParseException(string message, Exception inner) : base(message, inner) { }
}
