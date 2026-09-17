namespace Mlcp.Application.Onboarding;

/// <summary>
/// Facts about a tenant's own directory that MLCP learned from Microsoft, used to validate
/// user input against what the tenant actually owns.
/// </summary>
/// <remarks>
/// Populated from the <c>GET /organization</c> floor probe (<c>verifiedDomains</c>). Before
/// consent there is no app-only access, so the answer is usually "unknown" for a tenant that is
/// still asking its admin to connect; callers must treat null as unknown, never as "no domains".
/// </remarks>
public interface ITenantDirectoryInfo
{
    /// <summary>
    /// The tenant's verified domain names (lower case), from the last successful
    /// <c>GET /organization</c> probe, or null when they have not been discovered.
    /// </summary>
    Task<IReadOnlyCollection<string>?> GetVerifiedDomainsAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The default until capability discovery stores verified domains: everything is unknown.
/// </summary>
public sealed class UnknownTenantDirectoryInfo : ITenantDirectoryInfo
{
    public Task<IReadOnlyCollection<string>?> GetVerifiedDomainsAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyCollection<string>?>(null);
}
