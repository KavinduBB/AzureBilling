using Microsoft.Extensions.Logging;
using Mlcp.Domain.Sync;
using Mlcp.Shared.Resilience;

namespace Mlcp.Application.Sync;

/// <summary>
/// The one path by which synced data reaches a live table.
/// </summary>
/// <remarks>
/// <para>
/// Staging, then a validation gate, then a single-transaction merge on the natural key
/// (CLAUDE.md rule 6, docs/03-architecture.md §6.2). The ordering is the point: a live table is
/// never partially written, and a bad response from Microsoft cannot delete a customer's data
/// because it never reaches the live table at all.
/// </para>
/// <para>
/// The failure paths matter more than the success path. A validation failure, an exception and
/// a lost consent all leave the live tables byte-identical and record why on the run. Staging
/// is cleared in a finally block so a failed run cannot leave rows that a later run would pick
/// up and merge.
/// </para>
/// </remarks>
public sealed class SyncPipeline
{
    private readonly ISyncRunStore _runStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncPipeline> _logger;

    public SyncPipeline(ISyncRunStore runStore, TimeProvider timeProvider, ILogger<SyncPipeline> logger)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SyncRunOutcome> ExecuteAsync(
        Guid tenantId,
        ISyncJobHandler handler,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var run = await _runStore.StartRunAsync(tenantId, handler.JobType, correlationId, cancellationToken)
            .ConfigureAwait(false);

        var context = new SyncRunContext(tenantId, run.SyncRunId, handler.JobType, correlationId, run.ContinuationToken);

        try
        {
            var staged = await handler.FetchToStagingAsync(context, cancellationToken).ConfigureAwait(false);

            var baseline = await _runStore
                .GetLastSuccessfulRecordCountAsync(tenantId, handler.JobType, cancellationToken)
                .ConfigureAwait(false);

            var verdict = SyncValidationGate.Evaluate(
                new SyncValidationInput(handler.JobType, staged.StagedRowCount, baseline, staged.FieldViolations));

            if (!verdict.Passed)
            {
                _logger.LogError(
                    "Validation gate rejected {JobType} for tenant {TenantId}: {Notes}",
                    handler.JobType,
                    tenantId,
                    verdict.Notes);

                run.FailValidation(verdict.Notes, _timeProvider.GetUtcNow());
                await _runStore.SaveAsync(run, cancellationToken).ConfigureAwait(false);

                return new SyncRunOutcome(run, LivePublished: false);
            }

            var merged = await _runStore
                .InTransactionAsync(ct => handler.MergeStagingToLiveAsync(context, ct), cancellationToken)
                .ConfigureAwait(false);

            run.Succeed(merged, verdict.Notes, _timeProvider.GetUtcNow());
            await _runStore.SaveAsync(run, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "{JobType} completed for tenant {TenantId}: {Records} record(s) merged. {Notes}",
                handler.JobType,
                tenantId,
                merged,
                verdict.Notes);

            return new SyncRunOutcome(run, LivePublished: true);
        }
        catch (NeedsReconsentException ex)
        {
            // The tenant has already been flagged by the executor. Stop without retrying:
            // no amount of backoff restores a revoked grant.
            _logger.LogError(
                ex,
                "{JobType} aborted for tenant {TenantId}: consent or role missing.",
                handler.JobType,
                tenantId);

            run.AbortNeedsReconsent(ex.Message, _timeProvider.GetUtcNow());
            await _runStore.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);

            return new SyncRunOutcome(run, LivePublished: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Fail("Cancelled", "The run was cancelled before completion.", _timeProvider.GetUtcNow());
            await _runStore.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);

            throw;
        }
#pragma warning disable CA1031 // A sync worker must survive any single job failing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "{JobType} failed for tenant {TenantId}.", handler.JobType, tenantId);

            run.Fail(ex.GetType().Name, ex.Message, _timeProvider.GetUtcNow());
            await _runStore.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);

            return new SyncRunOutcome(run, LivePublished: false);
        }
        finally
        {
            // Staging must not outlive the run, or a later run would merge rows it never fetched.
            await SafeClearStagingAsync(handler, context).ConfigureAwait(false);
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
            _logger.LogWarning(
                ex,
                "Failed to clear staging for {JobType} run {SyncRunId}. Rows remain tagged and will be swept.",
                handler.JobType,
                context.SyncRunId);
        }
    }
}
