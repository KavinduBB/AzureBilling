using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Persistence.Stores;

/// <summary>
/// EF-backed store for the onboarding aggregate.
/// </summary>
/// <remarks>
/// Every read here goes through <see cref="MlcpDbContext"/> and is therefore already confined
/// to the tenant in scope. The tenant id parameters are for locating a row within that scope,
/// not for choosing a tenant: passing another tenant's id returns nothing rather than that
/// tenant's data.
/// </remarks>
public sealed class TenantOnboardingStore : ITenantOnboardingStore
{
    private readonly MlcpDbContext _context;
    private readonly TimeProvider _timeProvider;

    public TenantOnboardingStore(MlcpDbContext context, TimeProvider timeProvider)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        => _context.Tenants
            .Include(t => t.OnboardingSteps)
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken);

    public Task<TenantCapabilityProfile?> FindCapabilityProfileAsync(Guid tenantId, CancellationToken cancellationToken)
        => _context.TenantCapabilityProfiles
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);

    public async Task AddCapabilityProfileAsync(TenantCapabilityProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _context.TenantCapabilityProfiles.AddAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the step row, creating it if this is the first time the step has been reached.
    /// </summary>
    /// <remarks>
    /// Discovery is re-run on connect, weekly, on each unlock and after an auth failure, so
    /// this has to be safe to call repeatedly. The unique index on (TenantId, Step) is the
    /// backstop if two runs race.
    /// </remarks>
    public async Task<OnboardingStep> GetOrCreateStepAsync(
        Guid tenantId,
        OnboardingStepName step,
        CancellationToken cancellationToken)
    {
        var existing = await _context.OnboardingSteps
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Step == step, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var created = OnboardingStep.Pending(tenantId, step, _timeProvider.GetUtcNow());
        await _context.OnboardingSteps.AddAsync(created, cancellationToken).ConfigureAwait(false);

        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
        => _context.SaveChangesAsync(cancellationToken);
}
