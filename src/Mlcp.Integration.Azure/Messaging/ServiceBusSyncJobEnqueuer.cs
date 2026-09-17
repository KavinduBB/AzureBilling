using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Sync;
using Mlcp.Domain.Common;
using Mlcp.Domain.Sync;

namespace Mlcp.Integration.Azure.Messaging;

/// <summary>Configuration for the sync job queue.</summary>
public sealed record SyncQueueOptions
{
    public const string DefaultQueueName = "mlcp-sync-jobs";

    public string QueueName { get; init; } = DefaultQueueName;

    /// <summary>True when a Service Bus namespace or connection string is configured.</summary>
    public bool IsConfigured { get; init; }
}

/// <summary>Re-sends a message the consumer has decided to run again later.</summary>
public interface ISyncJobRequeuer
{
    Task RequeueAsync(SyncJobMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// The one sender for sync jobs, used by the web host (consent verification, discovery after an
/// unlock) and the worker (scheduler passes, retries).
/// </summary>
/// <remarks>
/// Session id = tenant id (ordering within a tenant); MessageId = <c>{tenant}:{job}:{dedupKey}</c>
/// (duplicate detection); <c>ScheduledEnqueueTime</c> = not-before, so delayed work never sleeps
/// in a request or a handler; CorrelationId set; the attempt counters travel in the body and as
/// application properties.
/// </remarks>
public sealed class ServiceBusSyncJobEnqueuer : ISyncJobEnqueuer, ISyncJobRequeuer, IAsyncDisposable
{
    public const string AttemptProperty = "mlcp-attempt";

    public const string ThrottleCountProperty = "mlcp-throttle-count";

    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusSyncJobEnqueuer> _logger;

    public ServiceBusSyncJobEnqueuer(
        ServiceBusClient client,
        SyncQueueOptions options,
        ILogger<ServiceBusSyncJobEnqueuer> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        _sender = client.CreateSender(options.QueueName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task EnqueueAsync(
        Guid tenantId,
        SyncJobType jobType,
        string deduplicationKey,
        DateTimeOffset? notBeforeUtc,
        string correlationId,
        CancellationToken cancellationToken)
        => SendAsync(
            new SyncJobMessage(tenantId, jobType, deduplicationKey, correlationId) { NotBeforeUtc = notBeforeUtc },
            cancellationToken);

    public Task RequeueAsync(SyncJobMessage message, CancellationToken cancellationToken)
        => SendAsync(message, cancellationToken);

    public async Task SendAsync(SyncJobMessage message, CancellationToken cancellationToken)
    {
        var serviceBusMessage = ToServiceBusMessage(message);

        await _sender.SendMessageAsync(serviceBusMessage, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Enqueued {JobType} for tenant {TenantId} as {MessageId}, not before {NotBefore:u}, attempt {Attempt}, correlation {CorrelationId}.",
            message.JobType,
            message.TenantId,
            serviceBusMessage.MessageId,
            message.NotBeforeUtc,
            message.Attempt,
            message.CorrelationId);
    }

    /// <summary>Builds the broker message. Public for tests.</summary>
    public static ServiceBusMessage ToServiceBusMessage(SyncJobMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.TenantId == Guid.Empty)
        {
            throw new ArgumentException("A sync job needs a tenant.", nameof(message));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message.DeduplicationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.CorrelationId);

        var serviceBusMessage = new ServiceBusMessage(JsonSerializer.Serialize(message, DomainJson.Options))
        {
            MessageId = message.MessageId,
            SessionId = message.SessionId,
            Subject = message.JobType.ToString(),
            CorrelationId = message.CorrelationId,
            ContentType = "application/json",
        };

        if (message.NotBeforeUtc is { } notBefore)
        {
            serviceBusMessage.ScheduledEnqueueTime = notBefore;
        }

        serviceBusMessage.ApplicationProperties[AttemptProperty] = message.Attempt;
        serviceBusMessage.ApplicationProperties[ThrottleCountProperty] = message.ThrottleCount;

        return serviceBusMessage;
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync().ConfigureAwait(false);
}

/// <summary>
/// Logs enqueues instead of sending them, for a developer machine without the Service Bus
/// emulator. Jobs are not executed.
/// </summary>
public sealed class LoggingSyncJobEnqueuer : ISyncJobEnqueuer, ISyncJobRequeuer
{
    private readonly ILogger<LoggingSyncJobEnqueuer> _logger;

    public LoggingSyncJobEnqueuer(ILogger<LoggingSyncJobEnqueuer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task EnqueueAsync(
        Guid tenantId,
        SyncJobType jobType,
        string deduplicationKey,
        DateTimeOffset? notBeforeUtc,
        string correlationId,
        CancellationToken cancellationToken)
        => RequeueAsync(
            new SyncJobMessage(tenantId, jobType, deduplicationKey, correlationId) { NotBeforeUtc = notBeforeUtc },
            cancellationToken);

    public Task RequeueAsync(SyncJobMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        _logger.LogWarning(
            "No Service Bus configured. Would enqueue {JobType} for tenant {TenantId} as {MessageId} not before {NotBefore:u}.",
            message.JobType,
            message.TenantId,
            message.MessageId,
            message.NotBeforeUtc);

        return Task.CompletedTask;
    }
}

public static class ServiceBusRegistration
{
    public const string NamespaceKey = "Mlcp:ServiceBus:FullyQualifiedNamespace";

    public const string ConnectionStringName = "ServiceBus";

    public const string QueueNameKey = "Sync:JobQueueName";

    /// <summary>
    /// Registers <see cref="ISyncJobEnqueuer"/> (and <see cref="ISyncJobRequeuer"/>) for either host.
    /// </summary>
    /// <remarks>
    /// <c>Mlcp:ServiceBus:FullyQualifiedNamespace</c> with <see cref="DefaultAzureCredential"/> in
    /// Azure; <c>ConnectionStrings:ServiceBus</c> only for the local emulator, which has no
    /// identity support; neither → <see cref="LoggingSyncJobEnqueuer"/>. The queue name is
    /// <c>Sync:JobQueueName</c> (default <c>mlcp-sync-jobs</c>). A <see cref="ServiceBusClient"/>
    /// and <see cref="SyncQueueOptions"/> are registered for the worker's consumer.
    /// </remarks>
    public static IServiceCollection AddMlcpServiceBusEnqueuer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var fullyQualifiedNamespace = configuration[NamespaceKey];
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        var hasNamespace = !string.IsNullOrWhiteSpace(fullyQualifiedNamespace);
        var hasConnectionString = !string.IsNullOrWhiteSpace(connectionString);

        var options = new SyncQueueOptions
        {
            QueueName = string.IsNullOrWhiteSpace(configuration[QueueNameKey])
                ? SyncQueueOptions.DefaultQueueName
                : configuration[QueueNameKey]!,
            IsConfigured = hasNamespace || hasConnectionString,
        };

        services.TryAddSingleton(options);

        if (!options.IsConfigured)
        {
            services.TryAddSingleton<LoggingSyncJobEnqueuer>();
            services.TryAddSingleton<ISyncJobEnqueuer>(sp => sp.GetRequiredService<LoggingSyncJobEnqueuer>());
            services.TryAddSingleton<ISyncJobRequeuer>(sp => sp.GetRequiredService<LoggingSyncJobEnqueuer>());
            return services;
        }

        services.TryAddSingleton(_ => hasNamespace
            ? new ServiceBusClient(fullyQualifiedNamespace, new DefaultAzureCredential())
            : new ServiceBusClient(connectionString));

        services.TryAddSingleton<ServiceBusSyncJobEnqueuer>();
        services.TryAddSingleton<ISyncJobEnqueuer>(sp => sp.GetRequiredService<ServiceBusSyncJobEnqueuer>());
        services.TryAddSingleton<ISyncJobRequeuer>(sp => sp.GetRequiredService<ServiceBusSyncJobEnqueuer>());

        return services;
    }
}
