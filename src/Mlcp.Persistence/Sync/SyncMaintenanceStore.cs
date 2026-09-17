using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Sync;
using Mlcp.Domain.Sync;

namespace Mlcp.Persistence.Sync;

/// <summary>
/// Cross-tenant sync housekeeping: abandoning stuck runs and sweeping dead staging.
/// </summary>
/// <remarks>
/// Uses the system context because both sweeps have to see every tenant's runs. Each call
/// creates and disposes its own context, so a sweep never shares a unit of work with anything.
/// </remarks>
public sealed partial class SyncMaintenanceStore : ISyncMaintenanceStore
{
    /// <summary>
    /// Staging is kept this long after its run was last touched: the resume window plus an hour.
    /// </summary>
    /// <remarks>
    /// The hour keeps the sweeper off rows a new run may be adopting right at the edge of the
    /// window. Adoption only picks runs touched within <see cref="SyncPipeline.ResumeWindow"/>
    /// and then touches them again, so the two can never both act on the same rows.
    /// </remarks>
    public static readonly TimeSpan StagingRetention = SyncPipeline.ResumeWindow + TimeSpan.FromHours(1);

    private readonly ISystemDbContextFactory _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncMaintenanceStore> _logger;

    public SyncMaintenanceStore(
        ISystemDbContextFactory contextFactory,
        TimeProvider timeProvider,
        ILogger<SyncMaintenanceStore> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> AbandonStuckRunsAsync(CancellationToken cancellationToken)
    {
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            var nowUtc = _timeProvider.GetUtcNow();

            // The shortest threshold bounds the query; each run is then judged by its own job's.
            var shortest = Enum.GetValues<SyncJobType>().Min(j => j.StuckAfter());
            var cutoff = nowUtc - shortest;

            var candidates = await context.SyncRuns
                .Where(r => r.Status == SyncRunStatus.Running && r.StartedUtc < cutoff)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var abandoned = 0;

            foreach (var run in candidates)
            {
                var stuckAfter = run.JobType.StuckAfter();

                if (nowUtc - run.StartedUtc <= stuckAfter)
                {
                    continue;
                }

                run.Abandon(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Still running {nowUtc - run.StartedUtc:c} after starting, beyond {stuckAfter:c} (twice the job's maximum duration). The worker is presumed dead."),
                    nowUtc);

                try
                {
                    // One save per run, so a run that finished in the meantime conflicts alone.
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    abandoned++;
                    LogAbandoned(run.JobType, run.TenantId, run.SyncRunId, run.StartedUtc);
                }
                catch (DbUpdateConcurrencyException)
                {
                    context.Entry(run).State = EntityState.Detached;
                    LogFinishedMeanwhile(run.SyncRunId);
                }
            }

            return abandoned;
        }
    }

    public async Task<int> SweepStagingAsync(CancellationToken cancellationToken)
    {
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            var cutoff = _timeProvider.GetUtcNow() - StagingRetention;
            var deleted = await new SqlStagingStore(context).SweepAsync(cutoff, cancellationToken).ConfigureAwait(false);

            if (deleted > 0)
            {
                LogSwept(deleted, cutoff);
            }

            return deleted;
        }
    }

    [LoggerMessage(EventId = 4160, Level = LogLevel.Warning,
        Message = "Abandoned {JobType} run {SyncRunId} for tenant {TenantId}, running since {StartedUtc}.")]
    private partial void LogAbandoned(SyncJobType jobType, Guid tenantId, Guid syncRunId, DateTimeOffset startedUtc);

    [LoggerMessage(EventId = 4161, Level = LogLevel.Information,
        Message = "Run {SyncRunId} changed while being abandoned; leaving it to its worker.")]
    private partial void LogFinishedMeanwhile(Guid syncRunId);

    [LoggerMessage(EventId = 4162, Level = LogLevel.Information,
        Message = "Swept {Deleted} staging row(s) of runs untouched since {Cutoff}.")]
    private partial void LogSwept(int deleted, DateTimeOffset cutoff);
}
