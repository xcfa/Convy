using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Convy.Services.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Convy.Ui;

/// <summary>
/// Copies every log event into the <see cref="LogBuffer"/> the UI reads, with its structured
/// properties (message template and scopes, e.g. <see cref="LogScopes.JobId"/>).
/// </summary>
public sealed class LogBufferSink : ILogEventSink
{
    // Same message rendering as the console: strings without quotes, structures as JSON.
    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:lj}");

    // Added by ASP.NET Core to every request's events; noise next to Convy's own properties.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "SourceContext", "EventId", "RequestId", "RequestPath", "ConnectionId", "ActionId", "ActionName", "TraceId", "SpanId", "ParentId",
    };

    private readonly LogBuffer _buffer;

    public LogBufferSink(LogBuffer buffer) => _buffer = buffer;

    public void Emit(LogEvent logEvent)
    {
        var category = logEvent.Properties.TryGetValue("SourceContext", out var context) && context is ScalarValue { Value: string name }
            ? name
            : null;

        using var message = new StringWriter();
        MessageFormatter.Format(logEvent, message);

        var properties = logEvent.Properties
            .Where(p => !Skipped.Contains(p.Key))
            .ToDictionary(p => p.Key, p => Simplify(p.Value), StringComparer.Ordinal);

        _buffer.Add(
            logEvent.Timestamp,
            (LogSeverity)(int)logEvent.Level,
            category,
            message.ToString(),
            logEvent.Exception?.ToString(),
            properties);
    }

    /// <summary>A property as a plain value that serializes to JSON: numbers, text, lists and maps.</summary>
    private static object? Simplify(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: null } => null,
        ScalarValue { Value: string or bool or int or long or double or decimal or float or short or byte or uint or ulong } scalar
            => scalar.Value,
        ScalarValue { Value: DateTimeOffset or DateTime } scalar => scalar.Value,
        ScalarValue scalar => scalar.Value.ToString(),
        SequenceValue sequence => sequence.Elements.Select(Simplify).ToList(),
        StructureValue structure => structure.Properties.ToDictionary(p => p.Name, p => Simplify(p.Value)),
        DictionaryValue dictionary => dictionary.Elements.ToDictionary(e => e.Key.Value?.ToString() ?? string.Empty, e => Simplify(e.Value)),
        _ => value.ToString(),
    };
}
