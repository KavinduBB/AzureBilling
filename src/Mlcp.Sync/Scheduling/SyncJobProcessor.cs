using Mlcp.Application.Onboarding;
using Mlcp.Application.Sync;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.Integration.Azure.Messaging;
using Mlcp.Shared.Resilience;

namespace Mlcp.Sync.Scheduling;

/// <summary>What the consumer should do with the message it delivered.</summary>
public enum SyncJobDisposition
{
    /// <summary>Settle the message. Any follow-up has already been enqueued.</summary>
    Complete = 0,

    /// <summary>The job has failed too often, or can never run.</summary>
    DeadLetter = 1,
}

/// <param name="Disposition">How to settle the delivered message.</param>
/// <param name="Reason">Why, for the log and the dead-letter reason.</param>
/// <param name="FollowUp">A copy re-queued for later (retry or throttle), if any.</param>
public sealed record SyncJobResult(SyncJobDisposition Disposition, string Reason, SyncJobMessage? FollowUp = null);

/// <summary>
/// Runs one sync job for the tenant bound to the current scope and decides how to settle it.
/// </summary>
/// <remarks>
/// <para>
/// Gate: only sync-eligible tenants run jobs, except <see cref="SyncJobType.ReconsentProbe"/>
/// (NeedsReconsent only), <see cref="SyncJobType.ConsentVerification"/>
/// (ConsentPendingVerification or NeedsReconsent) and <see cref="SyncJobType.TenantDeletion"/>.
/// A job for an ineligible tenant is completed and logged, never retried.
/// </para>
/// <para>
/// Delays never sleep in the handler (ADR-016 rule 7). Throttling completes the message and
/// re-enqueues a copy at <c>now + RetryAfter</c>; any other failure re-enqueues a copy with
/// exponential backoff, up to <see cref="MaxAttempts"/>, then dead-letters.
/// </para>
/// </remarks>
public sealed class SyncJobProcessor
{
    public const int MaxAttempts = 5;

    /// <summary>Throttle re-queues are not failures, but they are bounded too.</summary>
    public const int MaxThrottleRequeues = 20;

    private readonly ITenantOnboardingStore _store;
    private readonly CapabilityDiscoveryService _discovery;
    private readonly IConsentVerifier _consentVerifier;
    private readonly ISyncJobEnqueuer _enqueuer;
    private readonly ISyncJobRequeuer _requeuer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncJobProcessor> _logger;

    public SyncJobProcessor(
        ITenantOnboardingStore store,
        CapabilityDiscoveryService discovery,
        IConsentVerifier consentVerifier,
        ISyncJobEnqueuer enqueuer,
        ISyncJobRequeuer requeuer,
        TimeProvider timeProvider,
        ILogger<SyncJobProcessor> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _consentVerifier = consentVerifier ?? throw new ArgumentNullException(nameof(consentVerifier));
        _enqueuer = enqueuer ?? throw new ArgumentNullException(nameof(enqueuer));
        _requeuer = requeuer ?? throw new ArgumentNullException(nameof(requeuer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether <paramref name="jobType"/> may run for a tenant in <paramref name="status"/>.</summary>
    public static bool IsAllowed(SyncJobType jobType, TenantStatus status) => jobType switch
    {
        SyncJobType.TenantDeletion => true,
        SyncJobType.ReconsentProbe => status == TenantStatus.NeedsReconsent,
        SyncJobType.ConsentVerification => status is TenantStatus.ConsentPendingVerification or TenantStatus.NeedsReconsent,
        _ => status is TenantStatus.Provisioning or TenantStatus.Active,
    };

    public async Task<SyncJobResult> ProcessAsync(SyncJobMessage job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            return await RunAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MicrosoftThrottledException ex)
        {
            return await ThrottleAsync(job, ex.RetryAfter, ex.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (MicrosoftPlatformCredentialException ex)
        {
            return await ThrottleAsync(job, ex.RetryAfter, ex.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (MicrosoftCallRefusedException ex)
        {
            // A refusal is a verdict, not a fault; the owning service has recorded it. Retrying
            // cannot restore a grant.
            _logger.LogWarning("{JobType} for tenant {TenantId} was refused ({Kind}); completing.", job.JobType, job.TenantId, ex.Kind);
            return new SyncJobResult(SyncJobDisposition.Complete, $"Refused: {ex.Kind}");
        }
#pragma warning disable CA1031 // Every other failure is retried with backoff and then dead-lettered.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "{JobType} failed for tenant {TenantId} on attempt {Attempt}.", job.JobType, job.TenantId, job.Attempt);
            return await RetryAsync(job, SyncSchedule.RetryDelay(job.Attempt), ex.GetType().Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SyncJobResult> RunAsync(SyncJobMessage job, CancellationToken cancellationToken)
    {
        var tenant = await _store.FindTenantAsync(job.TenantId, cancellationToken).ConfigureAwait(false);

        if (tenant is null)
        {
            _logger.LogWarning("Completing {JobType}: tenant {TenantId} does not exist.", job.JobType, job.TenantId);
            return new SyncJobResult(SyncJobDisposition.Complete, "Tenant not found");
        }

        if (!IsAllowed(job.JobType, tenant.Status))
        {
            _logger.LogInformation(
                "Skipping {JobType} for tenant {TenantId}: status {Status} does not allow it.",
                job.JobType,
                job.TenantId,
                tenant.Status);

            return new SyncJobResult(SyncJobDisposition.Complete, $"Skipped: tenant is {tenant.Status}");
        }

        switch (job.JobType)
        {
            case SyncJobType.CapabilityDiscovery:
                var outcome = await _discovery.DiscoverAsync(job.TenantId, cancellationToken).ConfigureAwait(false);

                return outcome.ShouldRetry
                    ? await RetryAsync(job, outcome.RetryAfter ?? SyncSchedule.RetryDelay(job.Attempt), "Discovery inconclusive", cancellationToken).ConfigureAwait(false)
                    : new SyncJobResult(SyncJobDisposition.Complete, $"Discovery {outcome.Status}");

            case SyncJobType.ReconsentProbe:
                return await ReprobeAsync(job, cancellationToken).ConfigureAwait(false);

            case SyncJobType.ConsentVerification:
                return await VerifyConsentAsync(job, tenant, cancellationToken).ConfigureAwait(false);

            default:
                _logger.LogWarning("No handler is registered for {JobType}; completing without running it.", job.JobType);
                return new SyncJobResult(SyncJobDisposition.Complete, "No handler");
        }
    }

    private async Task<SyncJobResult> ReprobeAsync(SyncJobMessage job, CancellationToken cancellationToken)
    {
        var status = await _discovery.ReprobeFloorAsync(job.TenantId, job.CorrelationId, cancellationToken).ConfigureAwait(false);

        if (status == FloorReprobeStatus.Restored)
        {
            var now = _timeProvider.GetUtcNow();

            await _enqueuer.EnqueueAsync(
                job.TenantId,
                SyncJobType.CapabilityDiscovery,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"restored:{now:yyyyMMddHHmm}"),
                notBeforeUtc: null,
                job.CorrelationId,
                cancellationToken).ConfigureAwait(false);
        }

        return new SyncJobResult(SyncJobDisposition.Complete, $"Re-consent probe {status}");
    }

    private async Task<SyncJobResult> VerifyConsentAsync(SyncJobMessage job, Tenant tenant, CancellationToken cancellationToken)
    {
        var result = await _consentVerifier.VerifyAsync(job.TenantId, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (root, attempt) = ConsentVerificationAttempts.Parse(job.DeduplicationKey);

        switch (result.Status)
        {
            case ConsentVerificationStatus.Verified:
                var previous = tenant.Status;
                tenant.ConfirmConsent(tenant.ConsentGrantedByObjectId ?? Guid.Empty, now);
                await AuditConsentAsync(job, AuditOutcome.Succeeded, previous.ToString(), tenant.Status.ToString(), now, cancellationToken).ConfigureAwait(false);
                await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await _enqueuer.EnqueueAsync(
                    job.TenantId,
                    SyncJobType.CapabilityDiscovery,
                    $"consent:{root}",
                    notBeforeUtc: null,
                    job.CorrelationId,
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Consent verified for tenant {TenantId}; discovery enqueued.", job.TenantId);
                return new SyncJobResult(SyncJobDisposition.Complete, "Consent verified");

            case ConsentVerificationStatus.PendingPropagation when attempt < ConsentVerificationAttempts.MaxAttempts:
                var next = attempt + 1;

                await _enqueuer.EnqueueAsync(
                    job.TenantId,
                    SyncJobType.ConsentVerification,
                    ConsentVerificationAttempts.KeyFor(root, next),
                    now + ConsentVerificationAttempts.DelayBefore(next),
                    job.CorrelationId,
                    cancellationToken).ConfigureAwait(false);

                return new SyncJobResult(SyncJobDisposition.Complete, $"Consent still propagating; attempt {next} scheduled");

            case ConsentVerificationStatus.PendingPropagation:
                await AuditConsentAsync(job, AuditOutcome.Failed, tenant.Status.ToString(), "PendingPropagationExhausted", now, cancellationToken).ConfigureAwait(false);
                await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "Consent for tenant {TenantId} still not visible after {Attempts} attempts; leaving it {Status}.",
                    job.TenantId,
                    attempt,
                    tenant.Status);

                return new SyncJobResult(SyncJobDisposition.Complete, "Consent not confirmed after all attempts");

            case ConsentVerificationStatus.NotGranted:
                await AuditConsentAsync(job, AuditOutcome.Failed, tenant.Status.ToString(), "NotGranted", now, cancellationToken).ConfigureAwait(false);
                await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "Consent for tenant {TenantId} is not granted ({Detail}); leaving it {Status}.",
                    job.TenantId,
                    result.Detail,
                    tenant.Status);

                return new SyncJobResult(SyncJobDisposition.Complete, "Consent not granted");

            default:
                return await RetryAsync(job, SyncSchedule.RetryDelay(job.Attempt), $"Consent verification failed: {result.Detail}", cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The outcome row for a consent verification (ADR-018, ADR-019). Written on the same context
    /// as the tenant change and saved with it.
    /// </summary>
    private Task AuditConsentAsync(
        SyncJobMessage job,
        AuditOutcome outcome,
        string? oldValue,
        string? newValue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => _store.AddAuditAsync(
            AuditLog.ForSystem(
                job.TenantId,
                AuditAction.ConsentGranted,
                nameof(Tenant),
                job.TenantId.ToString(),
                outcome,
                job.CorrelationId,
                now)
                .WithValues(oldValue, newValue),
            cancellationToken);

    private async Task<SyncJobResult> RetryAsync(SyncJobMessage job, TimeSpan delay, string reason, CancellationToken cancellationToken)
    {
        if (job.Attempt >= MaxAttempts)
        {
            _logger.LogError(
                "{JobType} for tenant {TenantId} failed {Attempts} times; dead-lettering. Last reason: {Reason}.",
                job.JobType,
                job.TenantId,
                job.Attempt,
                reason);

            return new SyncJobResult(SyncJobDisposition.DeadLetter, reason);
        }

        var retry = job.ForRetry(_timeProvider.GetUtcNow() + delay);
        await _requeuer.RequeueAsync(retry, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "{JobType} for tenant {TenantId} will retry as attempt {Attempt} at {NotBefore:u}: {Reason}.",
            job.JobType,
            job.TenantId,
            retry.Attempt,
            retry.NotBeforeUtc,
            reason);

        return new SyncJobResult(SyncJobDisposition.Complete, reason, retry);
    }

    private async Task<SyncJobResult> ThrottleAsync(SyncJobMessage job, TimeSpan retryAfter, string reason, CancellationToken cancellationToken)
    {
        if (job.ThrottleCount >= MaxThrottleRequeues)
        {
            return await RetryAsync(job, SyncSchedule.RetryDelay(job.Attempt), "Throttled too many times: " + reason, cancellationToken)
                .ConfigureAwait(false);
        }

        var later = job.ForThrottle(_timeProvider.GetUtcNow() + retryAfter);
        await _requeuer.RequeueAsync(later, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "{JobType} for tenant {TenantId} throttled; re-queued for {NotBefore:u}.",
            job.JobType,
            job.TenantId,
            later.NotBeforeUtc);

        return new SyncJobResult(SyncJobDisposition.Complete, "Throttled: " + reason, later);
    }
}
