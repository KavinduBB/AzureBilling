using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Resilience;

namespace Mlcp.Persistence;

/// <summary>
/// Moves a tenant to <see cref="TenantStatus.NeedsReconsent"/> when Microsoft rejects its
/// credential, halting sync until an admin re-consents (CLAUDE.md rule 7).
/// </summary>
/// <remarks>
/// The write deliberately bypasses the tenant query filter through the system context. The
/// signal is raised from a sync worker whose scope is already bound to the affected tenant,
/// but it is also reachable from paths where it is not, and a flag that fails to be set is a
/// tenant that keeps hammering Microsoft with a dead credential.
/// </remarks>
public sealed class TenantReconsentSignal : ITenantReconsentSignal
{
    private readonly ISystemDbContextFactory _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantReconsentSignal> _logger;

    public TenantReconsentSignal(
        ISystemDbContextFactory contextFactory,
        TimeProvider timeProvider,
        ILogger<TenantReconsentSignal> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SignalNeedsReconsentAsync(
        Guid tenantId,
        MicrosoftProvider provider,
        int statusCode,
        CancellationToken cancellationToken)
    {
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            var tenant = await context.Tenants
                .FirstOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken)
                .ConfigureAwait(false);

            if (tenant is null)
            {
                _logger.LogWarning(
                    "Cannot flag tenant {TenantId} for re-consent: no such tenant.",
                    tenantId);

                return;
            }

            if (tenant.Status == TenantStatus.NeedsReconsent)
            {
                return;
            }

            tenant.MarkNeedsReconsent(_timeProvider.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogError(
                "Tenant {TenantId} flagged NeedsReconsent after {Status} from {Provider}. Sync is halted for this tenant.",
                tenantId,
                statusCode,
                provider);
        }
    }
}
