using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Common;
using Mlcp.Domain.Sync;
using Mlcp.Shared.Resilience;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Sync.Scheduling;

/// <summary>
/// Consumes sync jobs from Service Bus and runs them, one tenant at a time per session.
/// </summary>
/// <remarks>
/// <para>
/// A session processor rather than a plain one. Sessions are per tenant, so messages for a
/// single customer are handled in order while different customers run in parallel — which is
/// what stops a long-running enterprise sync from delaying every small tenant behind it.
/// </para>
/// <para>
/// Each message gets its own DI scope with the tenant context bound to that message's tenant.
/// The handler therefore sees exactly the same tenant confinement as a web request: the query
/// filter and <c>SESSION_CONTEXT</c> both apply, and a job cannot read another customer's rows
/// even though the worker as a whole can.
/// </para>
/// </remarks>
public sealed class SyncJobConsumer : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly SyncQueueOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyncJobConsumer> _logger;
    private ServiceBusSessionProcessor? _processor;

    public SyncJobConsumer(
        ServiceBusClient client,
        SyncQueueOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<SyncJobConsumer> logger)
    {
        _client = client;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = _client.CreateSessionProcessor(
            _options.QueueName,
            new ServiceBusSessionProcessorOptions
            {
                // Concurrency is across sessions, so this is "how many tenants at once", not
                // "how many jobs for one tenant at once".
                MaxConcurrentSessions = 8,
                MaxConcurrentCallsPerSession = 1,

                // Settled explicitly: a job that failed must not be completed, and one that
                // failed for a reason retrying cannot fix must not be retried five times.
                AutoCompleteMessages = false,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(30),
            });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync += HandleErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("Consuming sync jobs from {QueueName}.", _options.QueueName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a fault.
        }
    }

    private async Task HandleMessageAsync(ProcessSessionMessageEventArgs args)
    {
        SyncJobMessage? job;

        try
        {
            job = JsonSerializer.Deserialize<SyncJobMessage>(args.Message.Body.ToString(), DomainJson.Options);
        }
        catch (JsonException ex)
        {
            // Unparseable messages will never parse. Dead-letter immediately rather than
            // burning the delivery count and delaying every later message in the session.
            _logger.LogError(ex, "Discarding a sync job message that could not be deserialised.");
            await args.DeadLetterMessageAsync(args.Message, "InvalidPayload", ex.Message).ConfigureAwait(false);
            return;
        }

        if (job is null || job.TenantId == Guid.Empty || job.JobType == SyncJobType.Unknown)
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidPayload", "Missing tenant or job type")
                .ConfigureAwait(false);
            return;
        }

        using var scope = _scopeFactory.CreateScope();

        // Bind this scope to the job's tenant, exactly as the web middleware does for a request.
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(job.TenantId);

        try
        {
            await RunAsync(scope.ServiceProvider, job, args.CancellationToken).ConfigureAwait(false);
            await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);
        }
        catch (NeedsReconsentException)
        {
            // The tenant has already been flagged and its sync halted. Retrying cannot restore a
            // revoked grant, so the message is dead-lettered rather than redelivered.
            _logger.LogWarning(
                "Dead-lettering {JobType} for tenant {TenantId}: the tenant needs re-consent.",
                job.JobType,
                job.TenantId);

            await args.DeadLetterMessageAsync(args.Message, "NeedsReconsent", "Consent or role missing")
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Abandon and let Service Bus redeliver; the queue caps attempts.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "{JobType} failed for tenant {TenantId}; abandoning for redelivery.", job.JobType, job.TenantId);
            await args.AbandonMessageAsync(args.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches to the handler for this job type.
    /// </summary>
    /// <remarks>
    /// Capability discovery is the only job wired in Phase 0. The rest arrive with the providers
    /// that feed them in Phases 1 to 4; an unrecognised type is logged and completed rather than
    /// retried, because redelivering a job nothing can run only fills the dead-letter queue.
    /// </remarks>
    private async Task RunAsync(IServiceProvider services, SyncJobMessage job, CancellationToken cancellationToken)
    {
        switch (job.JobType)
        {
            case SyncJobType.CapabilityDiscovery:
                await services.GetRequiredService<CapabilityDiscoveryService>()
                    .DiscoverAsync(job.TenantId, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning(
                    "No handler is registered for {JobType}; completing the message without running it.",
                    job.JobType);
                break;
        }
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
