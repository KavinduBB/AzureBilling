using Mlcp.Application.Sync;

namespace Mlcp.Sync.Jobs;

/// <summary>
/// Shared loop for the periodic sync housekeeping services: run, log, wait, repeat, and never
/// let one failed pass stop the next.
/// </summary>
public abstract partial class SyncMaintenanceSweeper : BackgroundService
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    protected SyncMaintenanceSweeper(
        ISyncMaintenanceStore store,
        TimeProvider timeProvider,
        ILogger logger)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected ISyncMaintenanceStore Store { get; }

    /// <summary>Time between passes.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>Name used in log events.</summary>
    protected abstract string SweepName { get; }

    /// <summary>One pass. Returns the number of items it acted on.</summary>
    protected abstract Task<int> SweepOnceAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _timeProvider);

        do
        {
            try
            {
                var count = await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                LogPass(SweepName, count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed pass must not stop the worker; the next one retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPassFailed(ex, SweepName);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 4170, Level = LogLevel.Debug, Message = "{Sweep} pass acted on {Count} item(s).")]
    private partial void LogPass(string sweep, int count);

    [LoggerMessage(EventId = 4171, Level = LogLevel.Error, Message = "{Sweep} pass failed. Retrying on the next interval.")]
    private partial void LogPassFailed(Exception exception, string sweep);
}

/// <summary>
/// Marks runs left <c>Running</c> for more than twice their job's maximum duration as
/// <c>Abandoned</c> (ADR-024 §7), so they can be resumed and stop looking live.
/// </summary>
public sealed class StuckRunSweeper : SyncMaintenanceSweeper
{
    public StuckRunSweeper(ISyncMaintenanceStore store, TimeProvider timeProvider, ILogger<StuckRunSweeper> logger)
        : base(store, timeProvider, logger)
    {
    }

    /// <summary>Well under the shortest threshold (4 h), so a dead run is found promptly.</summary>
    protected override TimeSpan Interval => TimeSpan.FromMinutes(10);

    protected override string SweepName => nameof(StuckRunSweeper);

    protected override Task<int> SweepOnceAsync(CancellationToken cancellationToken)
        => Store.AbandonStuckRunsAsync(cancellationToken);
}

/// <summary>
/// Deletes staging rows that no run can resume any more (ADR-024 §8): those of runs that are
/// not running and have been untouched for longer than the resume window, and orphans.
/// </summary>
public sealed class StagingSweeper : SyncMaintenanceSweeper
{
    public StagingSweeper(ISyncMaintenanceStore store, TimeProvider timeProvider, ILogger<StagingSweeper> logger)
        : base(store, timeProvider, logger)
    {
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override string SweepName => nameof(StagingSweeper);

    protected override Task<int> SweepOnceAsync(CancellationToken cancellationToken)
        => Store.SweepStagingAsync(cancellationToken);
}
