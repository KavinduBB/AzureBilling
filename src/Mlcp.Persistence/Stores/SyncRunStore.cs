using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Sync;
using Mlcp.Domain.Sync;

namespace Mlcp.Persistence.Stores;

/// <summary>EF-backed store for sync run records and the pipeline's transaction boundary.</summary>
public sealed class SyncRunStore : ISyncRunStore
{
    private readonly MlcpDbContext _context;
    private readonly TimeProvider _timeProvider;

    public SyncRunStore(MlcpDbContext context, TimeProvider timeProvider)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<SyncRun> StartRunAsync(
        Guid tenantId,
        SyncJobType jobType,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var run = SyncRun.Start(tenantId, jobType, correlationId, _timeProvider.GetUtcNow());

        await _context.SyncRuns.AddAsync(run, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// Rows processed by the most recent successful run of this job.
    /// </summary>
    /// <remarks>
    /// Only <see cref="SyncRunStatus.Succeeded"/> runs count. A failed or gate-rejected run
    /// never published its rows, so using its count as the baseline would let a genuinely
    /// truncated response become the new normal on the following run — the gate would ratchet
    /// itself down instead of holding a line.
    /// </remarks>
    public async Task<int?> GetLastSuccessfulRecordCountAsync(
        Guid tenantId,
        SyncJobType jobType,
        CancellationToken cancellationToken)
    {
        var lastSuccess = await _context.SyncRuns
            .Where(r => r.TenantId == tenantId && r.JobType == jobType && r.Status == SyncRunStatus.Succeeded)
            .OrderByDescending(r => r.StartedUtc)
            .Select(r => (int?)r.RecordsProcessed)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return lastSuccess;
    }

    public Task SaveAsync(SyncRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Runs the merge inside one transaction, so a live table is never partially written.
    /// </summary>
    /// <remarks>
    /// An ambient transaction is honoured rather than nested: the sync worker may already have
    /// opened one, and SQL Server savepoints would not give the all-or-nothing guarantee that
    /// CLAUDE.md rule 6 requires of the merge step.
    /// </remarks>
    public async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_context.Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }

        var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (transaction.ConfigureAwait(false))
        {
            var result = await action(cancellationToken).ConfigureAwait(false);

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
    }
}
