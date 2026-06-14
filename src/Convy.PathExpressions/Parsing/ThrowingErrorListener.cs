using Antlr4.Runtime;

namespace Convy.PathExpressions.Parsing;

/// <summary>
/// Replaces ANTLR's default "print to console and recover" behaviour with a hard
/// failure, so a malformed line in ConvyMappings.ini surfaces as an exception
/// instead of being silently mis-parsed.
/// </summary>
internal sealed class ThrowingErrorListener : BaseErrorListener, IAntlrErrorListener<int>
{
    public static readonly ThrowingErrorListener Instance = new();

    public override void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        IToken offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e) =>
        throw new FilterParseException($"Syntax error at position {charPositionInLine}: {msg}");

    public void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        int offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e) =>
        throw new FilterParseException($"Syntax error at position {charPositionInLine}: {msg}");
}
