using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Mlcp.Domain.Common;

namespace Mlcp.Sync.Scheduling;

/// <summary>Configuration for the sync job queue.</summary>
public sealed record SyncQueueOptions
{
    public string QueueName { get; init; } = "mlcp-sync-jobs";
}

/// <summary>
/// Places sync jobs on Azure Service Bus.
/// </summary>
/// <remarks>
/// <para>
/// Every message carries a session id of the tenant and a message id that identifies the unit
/// of work. Sessions give ordering within a tenant while letting tenants run in parallel;
/// duplicate detection on the message id means a scheduler that restarts mid-pass, or two
/// schedulers overlapping, cannot double-run a tenant's sync.
/// </para>
/// <para>
/// Both properties are set here rather than left to the consumer, because the queue enforces
/// them and a message without them would be accepted and then processed out of order or twice.
/// </para>
/// </remarks>
public sealed class ServiceBusSyncJobDispatcher : ISyncJobDispatcher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusSyncJobDispatcher> _logger;

    public ServiceBusSyncJobDispatcher(
        ServiceBusClient client,
        SyncQueueOptions options,
        ILogger<ServiceBusSyncJobDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        _sender = client.CreateSender(options.QueueName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task DispatchAsync(SyncJobMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var serviceBusMessage = new ServiceBusMessage(JsonSerializer.Serialize(message, DomainJson.Options))
        {
            MessageId = message.MessageId,
            SessionId = message.SessionId,
            Subject = message.JobType.ToString(),
            CorrelationId = message.CorrelationId,
            ContentType = "application/json",
        };

        await _sender.SendMessageAsync(serviceBusMessage, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Dispatched {JobType} for tenant {TenantId} in slot {Slot:u}.",
            message.JobType,
            message.TenantId,
            message.ScheduledForUtc);
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync().ConfigureAwait(false);
}

/// <summary>
/// Records dispatches without a queue, for local development.
/// </summary>
/// <remarks>
/// Selected when no Service Bus connection is configured, so the scheduler's decisions can be
/// observed on a machine that is not running the emulator. Jobs are not executed.
/// </remarks>
public sealed class LoggingSyncJobDispatcher : ISyncJobDispatcher
{
    private readonly ILogger<LoggingSyncJobDispatcher> _logger;

    public LoggingSyncJobDispatcher(ILogger<LoggingSyncJobDispatcher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task DispatchAsync(SyncJobMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        _logger.LogInformation(
            "No Service Bus configured. Would dispatch {JobType} for tenant {TenantId} (message {MessageId}).",
            message.JobType,
            message.TenantId,
            message.MessageId);

        return Task.CompletedTask;
    }
}
