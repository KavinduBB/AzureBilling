namespace Mlcp.Application.Onboarding;

/// <summary>
/// Directory facts discovery has recorded for a tenant, read from the capability profile rather
/// than from Microsoft (CLAUDE.md rule 5). Contract shared with the onboarding work (ADR-018);
/// the integrator reconciles this file with the onboarding branch's copy.
/// </summary>
public interface ITenantDirectoryInfo
{
    /// <summary>
    /// The tenant's verified domain names (lower case), from the last successful
    /// <c>GET /organization</c> probe; null when discovery has not read them yet.
    /// </summary>
    Task<IReadOnlyCollection<string>?> GetVerifiedDomainsAsync(Guid tenantId, CancellationToken cancellationToken);
}
