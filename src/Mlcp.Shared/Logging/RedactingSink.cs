using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Mlcp.Shared.Logging;

/// <summary>
/// Wraps a sink so that every event it receives has already been redacted: its message
/// template, its properties, and its exception.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RedactionEnricher"/> covers properties only. Two parts of an event are out of an
/// enricher's reach:
/// </para>
/// <list type="bullet">
/// <item><b>The exception</b>, which every sink renders from <see cref="Exception.ToString"/>.
/// An SDK exception quoting a SAS URL or a token would otherwise reach the sink
/// untouched.</item>
/// <item><b>The template text.</b> An interpolated log call
/// (<c>LogInformation($"... {token}")</c>) puts the secret into the template rather than into
/// a property.</item>
/// </list>
/// <para>
/// This wrapper rebuilds the event with all three parts redacted before the inner sink sees
/// it. Doing it here rather than in each sink's formatter means it cannot be forgotten when a
/// sink is added through configuration.
/// </para>
/// </remarks>
public sealed class RedactingSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    private static readonly MessageTemplateParser TemplateParser = new();

    private readonly ILogEventSink _inner;

    public RedactingSink(ILogEventSink inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        _inner.Emit(Redact(logEvent));
    }

    /// <summary>Returns a copy of <paramref name="logEvent"/> with every secret shape removed.</summary>
    public static LogEvent Redact(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var template = logEvent.MessageTemplate;
        var redactedText = SensitiveDataRedactor.Redact(template.Text);

        if (!string.Equals(redactedText, template.Text, StringComparison.Ordinal))
        {
            template = TemplateParser.Parse(redactedText);
        }

        var properties = logEvent.Properties
            .Select(p => new LogEventProperty(p.Key, RedactionEnricher.RedactValue(p.Value) ?? p.Value))
            .ToList();

        var exception = logEvent.Exception is null or RedactedException
            ? logEvent.Exception
            : new RedactedException(logEvent.Exception);

        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception,
            template,
            properties,
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default);
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();

    public ValueTask DisposeAsync()
    {
        if (_inner is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Stands in for an exception on its way to a sink, carrying only redacted text.
/// </summary>
/// <remarks>
/// Sinks render exceptions with <see cref="ToString"/>, occasionally using
/// <see cref="Exception.Message"/> or <see cref="Exception.StackTrace"/>. All three are
/// overridden to return redacted text. <see cref="OriginalType"/> keeps the real type name for
/// diagnostics. The original exception is deliberately not kept as
/// <see cref="Exception.InnerException"/>: a sink that walks the chain would find the
/// unredacted text there.
/// </remarks>
public sealed class RedactedException : Exception
{
    private readonly string _rendered;
    private readonly string? _stackTrace;

    public RedactedException()
        : this(string.Empty)
    {
    }

    public RedactedException(string message)
        : base(SensitiveDataRedactor.Redact(message))
    {
        _rendered = base.Message;
        OriginalType = nameof(RedactedException);
    }

    public RedactedException(string message, Exception innerException)
        : this(message)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        _rendered = RedactedExceptionFormatting.Format(innerException);
        OriginalType = innerException.GetType().FullName ?? innerException.GetType().Name;
    }

    public RedactedException(Exception original)
        : base(SensitiveDataRedactor.Redact((original ?? throw new ArgumentNullException(nameof(original))).Message))
    {
        _rendered = RedactedExceptionFormatting.Format(original);
        _stackTrace = SensitiveDataRedactor.Redact(original.StackTrace);
        OriginalType = original.GetType().FullName ?? original.GetType().Name;
        HResult = original.HResult;
    }

    /// <summary>Full name of the exception type that was redacted.</summary>
    public string OriginalType { get; }

    public override string? StackTrace => _stackTrace;

    public override string ToString() => _rendered;
}
