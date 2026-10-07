namespace Convy.Services;

/// <summary>
/// A request was rejected because of its input or the current configuration (unknown
/// category, invalid sub-path, expired result, …). The message explains the reason and is
/// meant to be shown to the caller as is.
/// </summary>
public sealed class ConvyRequestException : Exception
{
    public ConvyRequestException(string message) : base(message) { }

    public ConvyRequestException(string message, Exception inner) : base(message, inner) { }
}
