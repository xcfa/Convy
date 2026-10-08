using System.IO;
using Convy.Services.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Convy.Ui;

/// <summary>Copies every log event into the <see cref="LogBuffer"/> the UI reads.</summary>
public sealed class LogBufferSink : ILogEventSink
{
    // Same message rendering as the console: strings without quotes, structures as JSON.
    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:lj}");

    private readonly LogBuffer _buffer;

    public LogBufferSink(LogBuffer buffer) => _buffer = buffer;

    public void Emit(LogEvent logEvent)
    {
        var category = logEvent.Properties.TryGetValue("SourceContext", out var context) && context is ScalarValue { Value: string name }
            ? name
            : null;

        using var message = new StringWriter();
        MessageFormatter.Format(logEvent, message);

        _buffer.Add(
            logEvent.Timestamp,
            (LogSeverity)(int)logEvent.Level,
            category,
            message.ToString(),
            logEvent.Exception?.ToString());
    }
}
