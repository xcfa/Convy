using System.Diagnostics;

namespace Convy.Services.Extensions;

/// <summary>
/// The time an interactive call (the REST API, a search, a file listing) leaves for the bot checks its requests run
/// into: a check is waited for only within it, less a margin for the answer, so the call still answers in time and
/// names the check that waits for a person. Without a budget (a download in the background) a check is waited for
/// <c>captcha.wait_sec</c>.
/// </summary>
public static class CallBudget
{
    /// <summary>The part of a call's time kept for its answer.</summary>
    private static readonly TimeSpan AnswerMargin = TimeSpan.FromSeconds(4);

    private static readonly AsyncLocal<(long Start, TimeSpan Budget)?> Deadline = new();

    /// <summary>The checks of this async flow are waited for only within <paramref name="callTime"/> from now. Dispose to end it.</summary>
    public static IDisposable Within(TimeSpan callTime)
    {
        var previous = Deadline.Value;
        Deadline.Value = (Stopwatch.GetTimestamp(), callTime - AnswerMargin);
        return new Restore(previous);
    }

    /// <summary>How long a check may be waited for now: <paramref name="wait"/>, cut to what the flow's budget leaves.</summary>
    public static TimeSpan Cut(TimeSpan wait)
    {
        if (Deadline.Value is not { } call)
        {
            return wait;
        }

        var left = call.Budget - Stopwatch.GetElapsedTime(call.Start);
        return left < wait ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : wait;
    }

    private sealed class Restore((long Start, TimeSpan Budget)? previous) : IDisposable
    {
        public void Dispose() => Deadline.Value = previous;
    }
}
