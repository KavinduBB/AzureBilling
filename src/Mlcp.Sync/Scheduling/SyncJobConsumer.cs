using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Mlcp.Domain.Common;
using Mlcp.Domain.Sync;
using Mlcp.Integration.Azure.Messaging;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Sync.Scheduling;

/// <summary>Consumer tunables.</summary>
public sealed record SyncConsumerOptions
{
    /// <summary>Tenants processed at once (sessions are per tenant).</summary>
    public int MaxConcurrentSessions { get; init; } = 8;

    /// <summary>
    /// The most one message may take. Worst case for discovery is roughly 20 calls, each with up to
    /// six 100-second attempts and hinted waits of up to 5 minutes; beyond this the job is
    /// cancelled and retried rather than holding a session forever.
    /// </summary>
    public TimeSpan MessageTimeout { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>Lock renewal must outlast <see cref="MessageTimeout"/> so a slow job never loses its lock.</summary>
    public TimeSpan MaxAutoLockRenewalDuration => MessageTimeout + TimeSpan.FromMinutes(15);
}

/// <summary>
/// Consumes sync jobs from Service Bus, one tenant at a time per session.
/// </summary>
/// <remarks>
/// <para>
/// Each message gets its own DI scope bound to the message's tenant, so a job sees the same
/// tenant confinement as a web request (query filter and <c>SESSION_CONTEXT</c>).
/// </para>
/// <para>
/// Messages are settled explicitly. <see cref="SyncJobProcessor"/> decides: retries and throttling
/// are handled by completing the delivered message and enqueuing a scheduled copy, never by
/// sleeping or abandoning (ADR-016 rule 7). Abandon is used only when the worker is shutting down
/// or the follow-up could not be sent, so the message is not lost.
/// </para>
/// </remarks>
public sealed class SyncJobConsumer : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly SyncQueueOptions _queueOptions;
    private readonly SyncConsumerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyncJobConsumer> _logger;
    private ServiceBusSessionProcessor? _processor;

    public SyncJobConsumer(
        ServiceBusClient client,
        SyncQueueOptions queueOptions,
        SyncConsumerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<SyncJobConsumer> logger)
    {
        _client = client;
        _queueOptions = queueOptions;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = _client.CreateSessionProcessor(
            _queueOptions.QueueName,
            new ServiceBusSessionProcessorOptions
            {
                MaxConcurrentSessions = _options.MaxConcurrentSessions,
                MaxConcurrentCallsPerSession = 1,
                AutoCompleteMessages = false,
                MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
            });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync += HandleErrorAsync;

        // A failure to start propagates: with BackgroundServiceExceptionBehavior.StopHost the
        // container exits and is restarted instead of idling with no consumer.
        await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("Consuming sync jobs from {QueueName}.", _queueOptions.QueueName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a fault.
        }
    }

    internal static SyncJobMessage? Parse(BinaryData body)
    {
        var job = JsonSerializer.Deserialize<SyncJobMessage>(body.ToString(), DomainJson.Options);

        return job is null
            || job.TenantId == Guid.Empty
            || job.JobType == SyncJobType.Unknown
            || string.IsNullOrWhiteSpace(job.DeduplicationKey)
            ? null
            : job;
    }

    private async Task HandleMessageAsync(ProcessSessionMessageEventArgs args)
    {
        SyncJobMessage? job;

        try
        {
            job = Parse(args.Message.Body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Dead-lettering sync job message {MessageId}: unparseable.", args.Message.MessageId);
            await args.DeadLetterMessageAsync(args.Message, "InvalidPayload", ex.Message).ConfigureAwait(false);
            return;
        }

        if (job is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidPayload", "Missing tenant, job type or key").ConfigureAwait(false);
            return;
        }

        var correlationId = string.IsNullOrWhiteSpace(args.Message.CorrelationId) ? job.CorrelationId : args.Message.CorrelationId;

        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TenantId"] = job.TenantId,
            ["JobType"] = job.JobType.ToString(),
            ["MessageId"] = args.Message.MessageId,
            ["Attempt"] = job.Attempt,
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(args.CancellationToken);
        timeout.CancelAfter(_options.MessageTimeout);

        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(job.TenantId);
        var processor = scope.ServiceProvider.GetRequiredService<SyncJobProcessor>();

        SyncJobResult result;

        try
        {
            result = await processor.ProcessAsync(job, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !args.CancellationToken.IsCancellationRequested)
        {
            // The job overran its budget. Treat it like any other failure.
            _logger.LogError("{JobType} for tenant {TenantId} exceeded {Timeout}.", job.JobType, job.TenantId, _options.MessageTimeout);
            await RequeueAfterTimeoutAsync(args, job).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return;
        }
#pragma warning disable CA1031 // The processor only throws when a follow-up could not be sent; keep the message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Could not settle {JobType} for tenant {TenantId}; abandoning for redelivery.", job.JobType, job.TenantId);
            await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (result.Disposition == SyncJobDisposition.DeadLetter)
        {
            await args.DeadLetterMessageAsync(args.Message, "RetriesExhausted", result.Reason).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("{JobType} for tenant {TenantId} settled: {Reason}.", job.JobType, job.TenantId, result.Reason);
        await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);
    }

    private async Task RequeueAfterTimeoutAsync(ProcessSessionMessageEventArgs args, SyncJobMessage job)
    {
        if (job.Attempt >= SyncJobProcessor.MaxAttempts)
        {
            await args.DeadLetterMessageAsync(args.Message, "RetriesExhausted", "Timed out").ConfigureAwait(false);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var requeuer = scope.ServiceProvider.GetRequiredService<ISyncJobRequeuer>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        await requeuer.RequeueAsync(job.ForRetry(clock.GetUtcNow() + SyncSchedule.RetryDelay(job.Attempt)), CancellationToken.None)
            .ConfigureAwait(false);

        await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "Service Bus error in {Operation} on {EntityPath}.",
            args.ErrorSource,
            args.EntityPath);

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken).ConfigureAwait(false);
            await _processor.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
