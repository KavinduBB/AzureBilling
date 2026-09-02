using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Persistence.Stores;

/// <summary>
/// Cross-tenant store for the deletion sweep.
/// </summary>
/// <remarks>
/// Runs under the system context, which is the only way to see tenants other than one's own. It
/// is registered exclusively in the sync worker; the web application never resolves it.
/// </remarks>
public sealed class TenantDeletionStore : ITenantDeletionStore
{
    private readonly ISystemDbContextFactory _contextFactory;

    public TenantDeletionStore(ISystemDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<IReadOnlyList<Tenant>> FindTenantsDueForDeletionAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            return await context.Tenants
                .AsNoTracking()
                .Where(t => t.Status == TenantStatus.GracePeriod
                    && t.DeleteScheduledUtc != null
                    && t.DeleteScheduledUtc <= asOfUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes every row for a tenant inside one transaction, returning per-table counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Child tables are deleted explicitly rather than relying only on cascade, so the counts
    /// that go into the certificate are measured rather than assumed. The order is
    /// children-first, which keeps the operation valid even where a relationship is configured
    /// to restrict rather than cascade.
    /// </para>
    /// <para>
    /// <c>ExecuteDeleteAsync</c> issues set-based DELETEs rather than loading entities. Loading
    /// a large tenant's rows into the change tracker in order to delete them would be slow and,
    /// for a tenant with millions of consumption facts, would not complete at all.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, int>> DeleteAllTenantDataAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (transaction.ConfigureAwait(false))
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["AuditLog"] = await context.AuditLogs
                        .Where(a => a.TenantId == tenantId)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),

                    ["SyncRun"] = await context.SyncRuns
                        .Where(r => r.TenantId == tenantId)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),

                    ["PendingConsentRequest"] = await context.PendingConsentRequests
                        .Where(r => r.TenantId == tenantId)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),

                    ["OnboardingStep"] = await context.OnboardingSteps
                        .Where(s => s.TenantId == tenantId)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),

                    ["AppUser"] = await context.AppUsers
                        .Where(u => u.TenantId == tenantId)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),

                    ["TenantCapabilityProfile"] = await context.TenantCapabilityProfiles
                        .Where(p => p.TenantId == tenantId)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),

                    // Last: everything above references it.
                    ["Tenant"] = await context.Tenants
                        .Where(t => t.TenantId == tenantId)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),
                };

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return counts;
            }
        }
    }

    public async Task AddCertificateAsync(DeletionCertificate certificate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            await context.DeletionCertificates.AddAsync(certificate, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
