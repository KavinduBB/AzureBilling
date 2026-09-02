using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Persistence.Stores;

/// <summary>EF-backed persistence for the onboarding flow.</summary>
public sealed class OnboardingRepository : IOnboardingRepository
{
    private readonly MlcpDbContext _context;
    private readonly TimeProvider _timeProvider;

    public OnboardingRepository(MlcpDbContext context, TimeProvider timeProvider)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        => _context.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken);

    public async Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await _context.Tenants.AddAsync(tenant, cancellationToken).ConfigureAwait(false);
    }

    public Task<AppUser?> FindAppUserAsync(Guid tenantId, Guid entraObjectId, CancellationToken cancellationToken)
        => _context.AppUsers
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.EntraObjectId == entraObjectId, cancellationToken);

    public Task<bool> HasAnyAppUserAsync(Guid tenantId, CancellationToken cancellationToken)
        => _context.AppUsers.AnyAsync(u => u.TenantId == tenantId, cancellationToken);

    public async Task AddAppUserAsync(AppUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        await _context.AppUsers.AddAsync(user, cancellationToken).ConfigureAwait(false);
    }

    public Task<PendingConsentRequest?> FindOpenConsentRequestAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        return _context.PendingConsentRequests
            .Where(r => r.TenantId == tenantId && r.CompletedUtc == null && r.ExpiresUtc > now)
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves a consent request from its emailed token, within the current tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This looks like it needs to read across tenants, and an earlier draft did. It does not.
    /// The requester and the administrator they emailed are both members of the same Entra
    /// tenant, so by the time the administrator has signed in, the tenant already in scope is
    /// the one that owns the request. The ordinary tenant filter applies.
    /// </para>
    /// <para>
    /// An administrator from a different tenant following a leaked link therefore sees the same
    /// "expired" page as someone using a stale token, which is the correct outcome and needs no
    /// special case. The token identifies a request; it authorises nothing on its own.
    /// </para>
    /// </remarks>
    public Task<PendingConsentRequest?> FindConsentRequestByTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult<PendingConsentRequest?>(null);
        }

        var now = _timeProvider.GetUtcNow();

        return _context.PendingConsentRequests
            .FirstOrDefaultAsync(
                r => r.Token == token && r.CompletedUtc == null && r.ExpiresUtc > now,
                cancellationToken);
    }

    public async Task AddConsentRequestAsync(PendingConsentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _context.PendingConsentRequests.AddAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAuditAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _context.AuditLogs.AddAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
        => _context.SaveChangesAsync(cancellationToken);
}
