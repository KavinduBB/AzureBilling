using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Mlcp.Shared.Logging;

/// <summary>
/// The one Serilog pipeline both hosts use, with redaction built in (CLAUDE.md rule 14, P0-4).
/// </summary>
/// <remarks>
/// <para>
/// The pipeline has four parts:
/// </para>
/// <list type="number">
/// <item>The root logger sets only the minimum levels, read from <c>Serilog:MinimumLevel</c>.
/// Nothing is written at the root.</item>
/// <item><see cref="RedactionEnricher"/> redacts properties on every event.</item>
/// <item>Every sink sits behind one <see cref="RedactingSink"/>, which also redacts the
/// message template and the exception.</item>
/// <item>Behind that wrapper, a child logger reads the full <c>Serilog</c> configuration
/// section and any sinks registered in DI, next to the console sink.</item>
/// </list>
/// <para>
/// Because configuration is read only behind the wrapper, a sink added later through
/// <c>appsettings</c> or Key Vault is redacted too. Nobody has to remember to redact it.
/// </para>
/// </remarks>
public static class MlcpSerilog
{
    public const string ConfigurationSection = "Serilog";

    /// <summary>
    /// Registers Serilog as the host's logger, configured by <see cref="ConfigureMlcpSerilog"/>.
    /// Also registers the <c>IDiagnosticContext</c> used by request logging.
    /// </summary>
    public static IServiceCollection AddMlcpSerilog(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddSerilog((serviceProvider, logger) => logger.ConfigureMlcpSerilog(configuration, serviceProvider));
    }

    /// <summary>
    /// Applies the MLCP pipeline to <paramref name="logger"/>.
    /// </summary>
    /// <param name="configuration">Application configuration; the <c>Serilog</c> section is read.</param>
    /// <param name="services">When given, sinks and enrichers registered in DI are included.</param>
    /// <param name="additionalSinks">
    /// Extra sinks, placed behind the redaction wrapper. Used by tests to capture output.
    /// </param>
    /// <param name="writeToConsole">False only in tests that capture output elsewhere.</param>
    public static LoggerConfiguration ConfigureMlcpSerilog(
        this LoggerConfiguration logger,
        IConfiguration configuration,
        IServiceProvider? services = null,
        Action<LoggerSinkConfiguration>? additionalSinks = null,
        bool writeToConsole = true)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);

        ApplyMinimumLevels(logger, configuration.GetSection(ConfigurationSection).GetSection("MinimumLevel"));

        logger
            .Enrich.FromLogContext()

            // Redaction is a pipeline stage, not a call-site discipline: a bearer token or SAS
            // URL reaching a sink because one developer forgot is not an acceptable failure mode.
            .Enrich.With<RedactionEnricher>();

        var redactedSinks = LoggerSinkConfiguration.Wrap(
                inner => new RedactingSink(inner),
                sinks =>
                {
                    sinks.Logger(child =>
                    {
                        child.MinimumLevel.Verbose();
                        child.ReadFrom.Configuration(configuration);

                        if (services is not null)
                        {
                            child.ReadFrom.Services(services);
                        }
                    });

                    if (writeToConsole)
                    {
                        sinks.Console(formatProvider: CultureInfo.InvariantCulture);
                    }

                    additionalSinks?.Invoke(sinks);
                });

        return logger.WriteTo.Sink(redactedSinks);
    }

    /// <summary>
    /// Reads <c>MinimumLevel</c> in either of its configuration forms: a scalar
    /// (<c>"MinimumLevel": "Warning"</c>), or an object with <c>Default</c> and
    /// <c>Override</c>. An unrecognised level fails at startup rather than being silently
    /// ignored.
    /// </summary>
    private static void ApplyMinimumLevels(LoggerConfiguration logger, IConfigurationSection section)
    {
        var defaultLevel = section.Value ?? section["Default"];

        logger.MinimumLevel.Is(defaultLevel is null ? LogEventLevel.Information : ParseLevel(defaultLevel, section.Path));

        foreach (var entry in section.GetSection("Override").GetChildren())
        {
            if (entry.Value is { } level)
            {
                logger.MinimumLevel.Override(entry.Key, ParseLevel(level, entry.Path));
            }
        }
    }

    private static LogEventLevel ParseLevel(string value, string path)
        => Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : throw new InvalidOperationException($"'{value}' at configuration key '{path}' is not a Serilog level.");
}
