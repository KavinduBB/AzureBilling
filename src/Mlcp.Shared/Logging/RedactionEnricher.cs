using Serilog.Core;
using Serilog.Events;

namespace Mlcp.Shared.Logging;

/// <summary>
/// Serilog enricher that rewrites every scalar string property of an event through
/// <see cref="SensitiveDataRedactor"/>.
/// </summary>
/// <remarks>
/// Placing redaction in the pipeline rather than at each call site means a secret cannot reach
/// a sink because one developer forgot. The rendered message is produced from the properties,
/// so redacting properties also redacts the message for structured sinks. Exception text and
/// the message template itself are outside the property bag, so they are redacted by
/// <see cref="RedactingSink"/>, which <see cref="MlcpSerilog"/> wraps around every sink.
/// </remarks>
public sealed class RedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        // Snapshot first: AddOrUpdateProperty mutates the collection being iterated.
        var properties = logEvent.Properties.ToArray();

        foreach (var (name, value) in properties)
        {
            if (RedactValue(value) is { } redacted)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    /// <summary>
    /// Returns a redacted replacement for <paramref name="value"/>, or null when it is
    /// unchanged. Recurses through sequences and structures so a secret nested inside a
    /// destructured object is caught too.
    /// </summary>
    internal static LogEventPropertyValue? RedactValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                {
                    var redacted = SensitiveDataRedactor.Redact(text);
                    return string.Equals(redacted, text, StringComparison.Ordinal) ? null : new ScalarValue(redacted);
                }

            case ScalarValue { Value: Uri uri }:
                {
                    var redacted = SensitiveDataRedactor.RedactUri(uri.ToString());
                    return string.Equals(redacted, uri.ToString(), StringComparison.Ordinal) ? null : new ScalarValue(redacted);
                }

            case SequenceValue sequence:
                {
                    LogEventPropertyValue[]? replacements = null;

                    for (var i = 0; i < sequence.Elements.Count; i++)
                    {
                        if (RedactValue(sequence.Elements[i]) is not { } replacement)
                        {
                            continue;
                        }

                        replacements ??= [.. sequence.Elements];
                        replacements[i] = replacement;
                    }

                    return replacements is null ? null : new SequenceValue(replacements);
                }

            case StructureValue structure:
                {
                    List<LogEventProperty>? replacements = null;

                    for (var i = 0; i < structure.Properties.Count; i++)
                    {
                        var property = structure.Properties[i];

                        if (RedactValue(property.Value) is not { } replacement)
                        {
                            continue;
                        }

                        replacements ??= [.. structure.Properties];
                        replacements[i] = new LogEventProperty(property.Name, replacement);
                    }

                    return replacements is null ? null : new StructureValue(replacements, structure.TypeTag);
                }

            case DictionaryValue dictionary:
                {
                    Dictionary<ScalarValue, LogEventPropertyValue>? replacements = null;

                    foreach (var (key, element) in dictionary.Elements)
                    {
                        if (RedactValue(element) is not { } replacement)
                        {
                            continue;
                        }

                        replacements ??= dictionary.Elements.ToDictionary(e => e.Key, e => e.Value);
                        replacements[key] = replacement;
                    }

                    return replacements is null ? null : new DictionaryValue(replacements);
                }

            default:
                return null;
        }
    }
}

/// <summary>
/// The exception side of redaction. Serilog keeps the <see cref="Exception"/> outside the
/// property bag, so <see cref="RedactingSink"/> replaces it with a
/// <see cref="RedactedException"/> whose text has passed through this method.
/// </summary>
public static class RedactedExceptionFormatting
{
    /// <summary>Renders an exception chain with every message and stack frame redacted.</summary>
    public static string Format(Exception? exception)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        return SensitiveDataRedactor.Redact(exception.ToString()) ?? string.Empty;
    }
}
