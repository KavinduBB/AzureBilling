using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Sync;
using Mlcp.Domain.Sync;

namespace Mlcp.Persistence.Stores;

/// <summary>EF-backed store for operator overrides of the validation gate (ADR-024 §4).</summary>
/// <remarks>
/// Overrides are tenant-scoped, so the global query filter and row-level security confine every
/// statement here to the tenant in scope, the conditional consume included.
/// </remarks>
public sealed class SyncGateOverrideStore : ISyncGateOverrideStore
{
    private readonly MlcpDbContext _context;

    public SyncGateOverrideStore(MlcpDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task AddAsync(SyncGateOverride approval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);

        await _context.SyncGateOverrides.AddAsync(approval, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<SyncGateOverride?> FindAvailableAsync(
        Guid tenantId,
        SyncJobType jobType,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
        => _context.SyncGateOverrides
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId
                && o.JobType == jobType
                && o.ConsumedBySyncRunId == null
                && o.ExpiresUtc > nowUtc)
            .OrderBy(o => o.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A single conditional UPDATE: of any number of concurrent consumers exactly one sees a
    /// row count of one. It runs in the caller's transaction, so a merge that rolls back
    /// releases the override again.
    /// </summary>
    public async Task<bool> TryConsumeAsync(
        Guid syncGateOverrideId,
        Guid syncRunId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Guid? consumer = syncRunId;
        DateTimeOffset? consumedUtc = nowUtc;

        var updated = await _context.SyncGateOverrides
            .Where(o => o.SyncGateOverrideId == syncGateOverrideId
                && o.ConsumedBySyncRunId == null
                && o.ExpiresUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(o => o.ConsumedBySyncRunId, consumer)
                    .SetProperty(o => o.ConsumedUtc, consumedUtc)
                    .SetProperty(o => o.UpdatedUtc, nowUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return updated == 1;
    }
}
