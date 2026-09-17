namespace Mlcp.Application.Onboarding;

/// <summary>The outcome of claiming a region for a tenant in the global directory.</summary>
/// <param name="Region">The region that holds the tenant after the call, whoever wrote it.</param>
/// <param name="RegisteredNow">True when this call created the entry.</param>
public sealed record RegionRegistration(string Region, bool RegisteredNow)
{
    /// <summary>True when the tenant belongs to a region other than <paramref name="currentRegion"/>.</summary>
    public bool IsElsewhere(string currentRegion)
        => !string.Equals(Region, currentRegion, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The global <c>tid → region</c> map shared by every regional stack (ADR-021).
/// </summary>
/// <remarks>
/// Holds only the tenant id, the region code and when it was registered: no names and no
/// customer data. A tenant's region is chosen once and never changed automatically, so the
/// write is a conditional insert, and whoever loses a race is told where the tenant went.
/// </remarks>
public interface IRegionDirectory
{
    /// <summary>The region holding <paramref name="tenantId"/>, or null when it is not registered anywhere.</summary>
    Task<string?> GetRegionAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Registers <paramref name="tenantId"/> in <paramref name="region"/> unless it is already
    /// registered, in which case the existing region is returned unchanged.
    /// </summary>
    Task<RegionRegistration> TryRegisterAsync(Guid tenantId, string region, CancellationToken cancellationToken);
}
