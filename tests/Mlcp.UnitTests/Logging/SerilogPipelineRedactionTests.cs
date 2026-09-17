using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mlcp.Shared.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;

namespace Mlcp.UnitTests.Logging;

/// <summary>
/// P0-4's acceptance criterion, end to end. A request carrying a bearer token, and an exception
/// quoting a SAS URL, are logged through the same pipeline the hosts use. The sink output must
/// show <c>[REDACTED]</c> and none of the secrets.
/// </summary>
/// <remarks>
/// The redactor's own tests prove the patterns. These prove the wiring: that templates,
/// properties and exception text all pass through it before any sink sees them, including a
/// sink the pipeline did not create itself.
/// </remarks>
public class SerilogPipelineRedactionTests
{
    private const string Jwt =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJodHRwczovL2dyYXBoLm1pY3Jvc29mdC5jb20ifQ.c2lnbmF0dXJlLXZhbHVl";

    private const string SasSignature = "c2FzU2lnbmF0dXJlU2VjcmV0";

    private const string SasUrl =
        "https://mlcpinvoices.blob.core.windows.net/inv/G001234.pdf?sv=2023-11-03&se=2026-09-02T12%3A00%3A00Z&sp=r&sig="
        + SasSignature;

    private static readonly string[] Secrets = [Jwt, SasSignature, "eyJhdWQiOiJodHRwczovL2dyYXBoLm1pY3Jvc29mdC5jb20ifQ"];

    private static IConfiguration Configuration(Dictionary<string, string?>? values = null)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:MinimumLevel:Override:Noisy"] = "Error",
            })
            .Build();

    private static (Serilog.Core.Logger Logger, StringWriter Output) BuildLogger(IServiceProvider? services = null, IConfiguration? configuration = null)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var formatter = new MessageTemplateTextFormatter(
            "[{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
            CultureInfo.InvariantCulture);

        var logger = new LoggerConfiguration()
            .ConfigureMlcpSerilog(
                configuration ?? Configuration(),
                services,
                additionalSinks: sinks => sinks.Sink(new TextSink(output, formatter)),
                writeToConsole: false)
            .CreateLogger();

        return (logger, output);
    }

    private static void AssertRedacted(string output)
    {
        output.Should().Contain(SensitiveDataRedactor.Placeholder);

        foreach (var secret in Secrets)
        {
            output.Should().NotContain(secret);
        }
    }

    [Fact]
    public void A_request_with_a_bearer_token_and_a_sas_url_in_an_exception_is_redacted()
    {
        var (logger, output) = BuildLogger();

        using (logger)
        {
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {Jwt}",
                ["ClientType"] = "Mlcp",
            };

            var exception = new HttpRequestException(
                $"Response status code does not indicate success: 403 (Forbidden) for {SasUrl}",
                new InvalidOperationException($"inner failure while fetching {SasUrl} with Authorization: Bearer {Jwt}"));

            logger.Error(
                exception,
                "Request {Method} {Url} failed with headers {@Headers}",
                "GET",
                SasUrl,
                headers);
        }

        var text = output.ToString();

        AssertRedacted(text);
        text.Should().Contain("G001234.pdf", "the document name is the useful diagnostic");
        text.Should().Contain("HttpRequestException", "the exception type must survive");
        text.Should().Contain("InvalidOperationException", "the inner exception must still be visible, redacted");
        text.Should().Contain("Mlcp", "ordinary headers survive");
    }

    [Fact]
    public void An_interpolated_message_template_is_redacted()
    {
        // The secret is in the template text, not in a property, which only the sink wrapper
        // can reach.
        var (logger, output) = BuildLogger();

        using (logger)
        {
#pragma warning disable CA2254 // Deliberately the anti-pattern under test.
            logger.Information($"Calling {SasUrl} with Authorization: Bearer {Jwt}");
#pragma warning restore CA2254
        }

        AssertRedacted(output.ToString());
    }

    [Fact]
    public void Logging_through_microsoft_extensions_logging_is_redacted()
    {
        // The hosts log through ILogger<T>, which reaches Serilog through its provider.
        var (logger, output) = BuildLogger();

        using (var factory = new SerilogLoggerFactory(logger, dispose: true))
        {
            var msLogger = factory.CreateLogger("Mlcp.Tests");

            msLogger.LogWarning(
                new InvalidOperationException($"Token {Jwt} rejected"),
                "Outbound call to {Uri} failed",
                new Uri(SasUrl));
        }

        AssertRedacted(output.ToString());
    }

    [Fact]
    public void A_sink_registered_in_dependency_injection_also_receives_redacted_events()
    {
        // Sinks the pipeline did not create must still sit behind the redaction wrapper.
        var captured = new CapturingSink();
        using var services = new ServiceCollection()
            .AddSingleton<ILogEventSink>(captured)
            .BuildServiceProvider();

        var (logger, _) = BuildLogger(services);

        using (logger)
        {
            logger.Error(new InvalidOperationException($"failed {SasUrl}"), "Auth header was {Header}", $"Bearer {Jwt}");
        }

        var logEvent = captured.Events.Should().ContainSingle().Subject;
        var rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture) + logEvent.Exception;

        AssertRedacted(rendered);
        logEvent.Exception.Should().BeOfType<RedactedException>()
            .Which.OriginalType.Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public void Minimum_levels_are_read_from_configuration()
    {
        var (logger, output) = BuildLogger();

        using (logger)
        {
            logger.Debug("debug-should-be-dropped");
            logger.ForContext(Constants.SourceContextPropertyName, "Noisy").Warning("noisy-warning-should-be-dropped");
            logger.Information("information-should-be-kept");
        }

        var text = output.ToString();
        text.Should().Contain("information-should-be-kept");
        text.Should().NotContain("debug-should-be-dropped");
        text.Should().NotContain("noisy-warning-should-be-dropped");
    }

    [Fact]
    public void A_scalar_minimum_level_is_honoured()
    {
        var (logger, output) = BuildLogger(configuration: Configuration(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel"] = "Warning",
        }));

        using (logger)
        {
            logger.Information("information-should-be-dropped");
            logger.Warning("warning-should-be-kept");
        }

        output.ToString().Should().Contain("warning-should-be-kept").And.NotContain("information-should-be-dropped");
    }

    [Fact]
    public void An_unrecognised_level_fails_at_startup()
    {
        var act = () => BuildLogger(configuration: Configuration(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Chatty",
        }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Chatty*");
    }

    [Fact]
    public void The_redacted_exception_carries_no_unredacted_inner_exception()
    {
        var original = new InvalidOperationException("outer", new InvalidOperationException($"inner {SasUrl}"));

        var redacted = new RedactedException(original);

        redacted.InnerException.Should().BeNull("a sink walking the chain would find the raw text there");
        redacted.ToString().Should().NotContain(SasSignature).And.Contain("inner");
        redacted.Message.Should().Be("outer");
    }

    [Fact]
    public void The_hosts_registration_resolves_a_working_logger()
    {
        // Both host builders call AddLogging before any of this runs.
        var services = new ServiceCollection().AddLogging();
        services.AddMlcpSerilog(Configuration());

        using var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<SerilogPipelineRedactionTests>>();
        logger.Should().NotBeNull();
        logger.IsEnabled(LogLevel.Information).Should().BeTrue();
        logger.IsEnabled(LogLevel.Debug).Should().BeFalse();
    }

    private sealed class TextSink(TextWriter writer, MessageTemplateTextFormatter formatter) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => formatter.Format(logEvent, writer);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
