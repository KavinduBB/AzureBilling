using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Onboarding;

namespace Mlcp.Persistence.Stores;

/// <summary>
/// Reads the verified domains recorded on the capability profile (column <c>VerifiedDomains</c>)
/// by the last successful organization probe (ADR-018 consent-request recipients).
/// </summary>
/// <remarks>
/// Runs under the scope's tenant filter, so another tenant's id returns null rather than data.
/// Every tenant has at least its initial <c>onmicrosoft.com</c> domain, so an empty list can only
/// mean "not read yet" and is reported as null.
/// </remarks>
public sealed class TenantDirectoryInfoStore : ITenantDirectoryInfo
{
    private readonly MlcpDbContext _context;

    public TenantDirectoryInfoStore(MlcpDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IReadOnlyCollection<string>?> GetVerifiedDomainsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var profile = await _context.TenantCapabilityProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

        return profile is { VerifiedDomains.Count: > 0 } ? profile.VerifiedDomains.ToList() : null;
    }
}
