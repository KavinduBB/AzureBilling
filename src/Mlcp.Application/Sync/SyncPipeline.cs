using Microsoft.Extensions.Logging;
using Mlcp.Domain.Sync;
using Mlcp.Shared.Resilience;

namespace Mlcp.Application.Sync;

/// <summary>
/// The one path by which synced data reaches a live table.
/// </summary>
/// <remarks>
/// <para>
/// Lock → start (or resume) → staging → validation gate → one transaction holding the MERGE and
/// the run's <c>Succeeded</c> status → staging cleanup (CLAUDE.md rule 6, ADR-024 §5). The
/// ordering is the point: a live table is never partially written, a bad response from
/// Microsoft never reaches it, and a run can never read <c>Failed</c> after its merge
/// committed, because the status commits with the merge.
/// </para>
/// <para>
/// Staging is cleared after a success and after a gate rejection. A failure that left a cursor
/// keeps its staging, so the next run can adopt it and continue (ADR-024 §8); the staging
/// sweeper removes it if nobody does within the resume window.
/// </para>
/// <para>
/// Once the transaction has committed nothing observes the caller's cancellation token, so a
/// shutdown arriving at that moment still yields a <c>Succeeded</c> outcome. Every save on a
/// failure path uses <see cref="CancellationToken.None"/> for the same reason: the record of
/// why a run stopped must survive the cancellation that stopped it.
/// </para>
/// </remarks>
public sealed partial class SyncPipeline
{
    /// <summary>How long a failed or abandoned run's staging stays adoptable (ADR-024 §8).</summary>
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromHours(24);

    private readonly ISyncRunStore _runStore;
    private readonly ISyncGateContext _gateContext;
    private readonly ISyncGateOverrideStore _overrides;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncPipeline> _logger;

    public SyncPipeline(
        ISyncRunStore runStore,
        ISyncGateContext gateContext,
        ISyncGateOverrideStore overrides,
        TimeProvider timeProvider,
        ILogger<SyncPipeline> logger)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _gateContext = gateContext ?? throw new ArgumentNullException(nameof(gateContext));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs a job in its default mode, for jobs that are not period-scoped.</summary>
    public Task<SyncRunOutcome> ExecuteAsync(
        Guid tenantId,
        ISyncJobHandler handler,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ExecuteAsync(new SyncRunRequest(tenantId, handler.JobType, correlationId), handler, cancellationToken);
    }

    public async Task<SyncRunOutcome> ExecuteAsync(
        SyncRunRequest request,
        ISyncJobHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId, nameof(request));

        if (request.JobType != handler.JobType)
        {
            throw new ArgumentException(
                $"Request is for {request.JobType} but the handler runs {handler.JobType}.",
                nameof(handler));
        }

        var runLock = await _runStore
            .TryAcquireLockAsync(request.TenantId, request.JobType, cancellationToken)
            .ConfigureAwait(false);

        if (runLock is null)
        {
            return await RecordSkippedAsync(request).ConfigureAwait(false);
        }

        await using (runLock.ConfigureAwait(false))
        {
            return await ExecuteLockedAsync(request, handler, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SyncRunOutcome> RecordSkippedAsync(SyncRunRequest request)
    {
        LogSkipped(request.JobType, request.TenantId);

        // Not cancellable: the request has already been decided, and the record is what shows an
        // operator that the job was deduplicated rather than lost.
        var skipped = await _runStore
            .RecordSkippedAsync(request, "Another worker holds the run lock for this tenant and job.", CancellationToken.None)
            .ConfigureAwait(false);

        return new SyncRunOutcome(skipped, LivePublished: false);
    }

    private async Task<SyncRunOutcome> ExecuteLockedAsync(
        SyncRunRequest request,
        ISyncJobHandler handler,
        CancellationToken cancellationToken)
    {
        var candidate = await _runStore
            .FindResumableRunAsync(request, _timeProvider.GetUtcNow() - ResumeWindow, cancellationToken)
            .ConfigureAwait(false);

        var run = await _runStore.StartRunAsync(request, candidate, cancellationToken).ConfigureAwait(false);

        if (run.ResumedFromSyncRunId is { } adopted)
        {
            LogResumed(request.JobType, request.TenantId, run.SyncRunId, adopted);
        }

        var context = new SyncRunContext(
            request.TenantId,
            run.SyncRunId,
            request.JobType,
            request.CorrelationId,
            run.ContinuationToken,
            new RunProgress(this, run))
        {
            LoadMode = run.LoadMode,
            PeriodKey = run.PeriodKey,
        };

        var clearStaging = false;

        try
        {
            var staged = await handler.FetchToStagingAsync(context, cancellationToken).ConfigureAwait(false);

            run.RecordStaged(staged.StagedRowCount, _timeProvider.GetUtcNow());
            await _runStore.SaveAsync(run, cancellationToken).ConfigureAwait(false);

            var (verdict, grant) = await JudgeAsync(run, staged, cancellationToken).ConfigureAwait(false);

            if (!verdict.Passed)
            {
                LogGateRejected(request.JobType, request.TenantId, verdict.Decision, verdict.Notes);

                clearStaging = true;
                run.FailValidation(verdict.Notes, _timeProvider.GetUtcNow());
                await SaveTerminalAsync(run).ConfigureAwait(false);

                return new SyncRunOutcome(run, LivePublished: false);
            }

            var consume = verdict.ConsumesOverride ? grant : null;
            var completedUtc = _timeProvider.GetUtcNow();

            await _runStore
                .CompleteInTransactionAsync(
                    run,
                    async ct =>
                    {
                        if (consume is not null
                            && !await _overrides.TryConsumeAsync(consume.SyncGateOverrideId, run.SyncRunId, completedUtc, ct).ConfigureAwait(false))
                        {
                            throw new SyncGateOverrideUnavailableException(consume.SyncGateOverrideId);
                        }

                        var affected = await handler.MergeStagingToLiveAsync(context, ct).ConfigureAwait(false);

                        // Deterministic on every attempt: the store resets the run before each one.
                        run.Succeed(affected, verdict.Notes, completedUtc);
                        return affected;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            // Committed. From here on nothing may observe the caller's token or fail the run.
            clearStaging = true;

            if (consume is not null)
            {
                LogOverrideConsumed(consume.SyncGateOverrideId, consume.ApprovedBy, request.JobType, request.TenantId, run.SyncRunId, verdict.Decision);
            }

            LogSucceeded(request.JobType, request.TenantId, run.StagedRowCount ?? 0, run.RecordsProcessed, verdict.Notes);

            return new SyncRunOutcome(run, LivePublished: true);
        }
        catch (SyncGateOverrideUnavailableException ex)
        {
            // Another run consumed the override, or it expired, between the gate and the merge.
            // The drop is no longer approved, so this is a gate rejection like any other.
            clearStaging = true;
            var notes = $"{ex.Message} Live data left untouched.";
            LogGateRejected(request.JobType, request.TenantId, SyncGateDecision.BlockedDrop, notes);

            FailIfRunning(run, r => r.FailValidation(notes, _timeProvider.GetUtcNow()));
            await SaveTerminalAsync(run).ConfigureAwait(false);

            return new SyncRunOutcome(run, LivePublished: false);
        }
        catch (NeedsReconsentException ex)
        {
            // Floor-level consent is gone (ADR-016). No amount of backoff restores it, and the
            // tenant aggregate belongs to the caller, so the decision is surfaced, not applied.
            LogReconsent(ex, request.JobType, request.TenantId);

            clearStaging = true;
            FailIfRunning(run, r => r.AbortNeedsReconsent(ex.Message, _timeProvider.GetUtcNow()));
            await SaveTerminalAsync(run).ConfigureAwait(false);

            return new SyncRunOutcome(run, LivePublished: false)
            {
                RequiresReconsent = true,
                ReconsentReason = ex.Message,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !run.IsSuccessful)
        {
            // Shutdown mid-run is the main reason to resume: keep staging if there is a cursor.
            FailIfRunning(run, r => r.Fail("Cancelled", "The run was cancelled before completion.", _timeProvider.GetUtcNow()));
            clearStaging = !run.IsResumable;
            await SaveTerminalAsync(run).ConfigureAwait(false);

            throw;
        }
#pragma warning disable CA1031 // A sync worker must survive any single job failing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (run.IsSuccessful)
            {
                // The merge and the status committed together; whatever failed afterwards
                // cannot un-publish them.
                LogFailed(ex, request.JobType, request.TenantId);
                return new SyncRunOutcome(run, LivePublished: true);
            }

            var retryAfter = (ex as IRetryLaterFailure)?.RetryAfter;

            if (retryAfter is { } wait)
            {
                LogThrottled(request.JobType, request.TenantId, wait);
            }
            else
            {
                LogFailed(ex, request.JobType, request.TenantId);
            }

            FailIfRunning(run, r => r.Fail(
                retryAfter is null ? ex.GetType().Name : "RetryLater",
                ex.Message,
                _timeProvider.GetUtcNow()));

            clearStaging = !run.IsResumable;
            await SaveTerminalAsync(run).ConfigureAwait(false);

            return new SyncRunOutcome(run, LivePublished: false) { RetryAfter = retryAfter };
        }
        finally
        {
            if (clearStaging)
            {
                // Rows left tagged with a finished run would never be merged, but they would be
                // counted by nothing and swept only after the resume window.
                await SafeClearStagingAsync(handler, context).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Evaluates the gate, looking up the invoice exemption only when a period-scoped drop needs
    /// it, and the override only when volume checks apply.
    /// </summary>
    private async Task<(SyncValidationResult Verdict, SyncGateOverrideGrant? Grant)> JudgeAsync(
        SyncRun run,
        StagingSummary staged,
        CancellationToken cancellationToken)
    {
        var baseline = await _runStore.GetBaselineAsync(run, cancellationToken).ConfigureAwait(false);

        SyncGateOverrideGrant? grant = null;

        if (run.LoadMode == SyncLoadMode.Full && staged.FieldViolations.Count == 0)
        {
            var available = await _overrides
                .FindAvailableAsync(run.TenantId, run.JobType, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            grant = available?.ToGrant();
        }

        var input = new SyncValidationInput(run.LoadMode, staged.StagedRowCount, baseline, staged.FieldViolations)
        {
            PeriodKey = run.PeriodKey,
            Override = grant,
        };

        var verdict = SyncValidationGate.Evaluate(input);

        if (verdict.IsVolumeBlock && run.PeriodKey is { } period && baseline is not null)
        {
            var invoiced = await _gateContext
                .InvoiceIssuedSinceAsync(run.TenantId, period, baseline.StartedUtc, cancellationToken)
                .ConfigureAwait(false);

            if (invoiced)
            {
                verdict = SyncValidationGate.Evaluate(input with { InvoiceIssuedSinceBaseline = true });
            }
        }

        return (verdict, grant);
    }

    private static void FailIfRunning(SyncRun run, Action<SyncRun> fail)
    {
        // The store guarantees a failed transaction leaves the run running. A run that is
        // already terminal here was finished by a path that has already recorded why.
        if (run.Status == SyncRunStatus.Running)
        {
            fail(run);
        }
    }

    private async Task SaveTerminalAsync(SyncRun run)
    {
        try
        {
            await _runStore.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Failing to record a failure must not mask the failure itself.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Most likely the stuck-run sweeper abandoned the run concurrently. The row stays
            // non-running either way, and the sweeper handles one that stays running.
            LogTerminalSaveFailed(ex, run.JobType, run.SyncRunId, run.Status);
        }
    }

    private async Task SafeClearStagingAsync(ISyncJobHandler handler, SyncRunContext context)
    {
        try
        {
            await handler.ClearStagingAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup failure must not mask the run's real outcome.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStagingClearFailed(ex, handler.JobType, context.SyncRunId);
        }
    }

    private async Task CheckpointAsync(SyncRun run, string continuationToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationToken);
        run.RecordContinuation(continuationToken, _timeProvider.GetUtcNow());
        await _runStore.SaveAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private sealed class RunProgress(SyncPipeline pipeline, SyncRun run) : ISyncRunProgress
    {
        public Task CheckpointAsync(string continuationToken, CancellationToken cancellationToken)
            => pipeline.CheckpointAsync(run, continuationToken, cancellationToken);
    }

    [LoggerMessage(EventId = 4100, Level = LogLevel.Information,
        Message = "{JobType} for tenant {TenantId} skipped: another worker holds the run lock.")]
    private partial void LogSkipped(SyncJobType jobType, Guid tenantId);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information,
        Message = "{JobType} run {SyncRunId} for tenant {TenantId} resumes the staging of run {AdoptedSyncRunId}.")]
    private partial void LogResumed(SyncJobType jobType, Guid tenantId, Guid syncRunId, Guid adoptedSyncRunId);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Error,
        Message = "Validation gate rejected {JobType} for tenant {TenantId} ({Decision}): {Notes}")]
    private partial void LogGateRejected(SyncJobType jobType, Guid tenantId, SyncGateDecision decision, string notes);

    /// <summary>The audit event for an operator override (ADR-024 §4). Kept at Warning so it is always retained.</summary>
    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning,
        Message = "AUDIT SyncGateOverrideConsumed: override {SyncGateOverrideId} approved by {ApprovedBy} consumed by {JobType} run {SyncRunId} for tenant {TenantId} ({Decision}).")]
    private partial void LogOverrideConsumed(Guid syncGateOverrideId, string approvedBy, SyncJobType jobType, Guid tenantId, Guid syncRunId, SyncGateDecision decision);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Information,
        Message = "{JobType} completed for tenant {TenantId}: {Staged} row(s) staged, {Merged} affected by merge. {Notes}")]
    private partial void LogSucceeded(SyncJobType jobType, Guid tenantId, int staged, int merged, string notes);

    [LoggerMessage(EventId = 4105, Level = LogLevel.Error,
        Message = "{JobType} aborted for tenant {TenantId}: floor-level consent is missing.")]
    private partial void LogReconsent(Exception exception, SyncJobType jobType, Guid tenantId);

    [LoggerMessage(EventId = 4106, Level = LogLevel.Warning,
        Message = "{JobType} for tenant {TenantId} throttled; Microsoft asked to retry after {RetryAfter}.")]
    private partial void LogThrottled(SyncJobType jobType, Guid tenantId, TimeSpan retryAfter);

    [LoggerMessage(EventId = 4107, Level = LogLevel.Error,
        Message = "{JobType} failed for tenant {TenantId}.")]
    private partial void LogFailed(Exception exception, SyncJobType jobType, Guid tenantId);

    [LoggerMessage(EventId = 4108, Level = LogLevel.Error,
        Message = "Could not record {JobType} run {SyncRunId} as {Status}.")]
    private partial void LogTerminalSaveFailed(Exception exception, SyncJobType jobType, Guid syncRunId, SyncRunStatus status);

    [LoggerMessage(EventId = 4109, Level = LogLevel.Warning,
        Message = "Failed to clear staging for {JobType} run {SyncRunId}. Rows remain tagged and will be swept.")]
    private partial void LogStagingClearFailed(Exception exception, SyncJobType jobType, Guid syncRunId);
}

/// <summary>
/// The override the gate relied on was consumed by another run or expired before the merge.
/// Thrown inside the merge transaction so it rolls back.
/// </summary>
public sealed class SyncGateOverrideUnavailableException : Exception
{
    public SyncGateOverrideUnavailableException()
    {
    }

    public SyncGateOverrideUnavailableException(string message)
        : base(message)
    {
    }

    public SyncGateOverrideUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SyncGateOverrideUnavailableException(Guid syncGateOverrideId)
        : base($"Operator override {syncGateOverrideId} was consumed by another run or expired before the merge.")
    {
        SyncGateOverrideId = syncGateOverrideId;
    }

    public Guid SyncGateOverrideId { get; }
}
