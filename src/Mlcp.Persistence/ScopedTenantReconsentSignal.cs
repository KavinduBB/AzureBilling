using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Resilience;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Persistence;

/// <summary>
/// Flags the current tenant for re-consent using the request's own scoped context.
/// </summary>
/// <remarks>
/// <para>
/// The web application probes Microsoft during onboarding, so it needs to react to a revoked
/// grant — but it has no business reading across tenants to do it. Inside a request the tenant
/// whose credential failed is always the tenant in scope, so the ordinary context is enough and
/// the system escape hatch stays out of the web host entirely.
/// </para>
/// <para>
/// A signal naming a different tenant is refused rather than silently ignored: it would mean a
/// probe ran for someone other than the signed-in customer, which is a defect worth surfacing.
/// </para>
/// </remarks>
public sealed class ScopedTenantReconsentSignal : ITenantReconsentSignal
{
    private readonly MlcpDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScopedTenantReconsentSignal> _logger;

    public ScopedTenantReconsentSignal(
        MlcpDbContext context,
        ITenantContext tenantContext,
        TimeProvider timeProvider,
        ILogger<ScopedTenantReconsentSignal> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SignalNeedsReconsentAsync(
        Guid tenantId,
        MicrosoftProvider provider,
        int statusCode,
        CancellationToken cancellationToken)
    {
        if (_tenantContext.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                $"Refusing to flag tenant {tenantId} for re-consent while tenant {_tenantContext.TenantId} is in scope.");
        }

        var tenant = await _context.Tenants
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (tenant is null || tenant.Status == TenantStatus.NeedsReconsent)
        {
            return;
        }

        tenant.MarkNeedsReconsent(_timeProvider.GetUtcNow());
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogError(
            "Tenant {TenantId} flagged NeedsReconsent after {Status} from {Provider}.",
            tenantId,
            statusCode,
            provider);
    }
}
