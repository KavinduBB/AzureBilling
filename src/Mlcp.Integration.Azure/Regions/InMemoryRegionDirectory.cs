using System.Collections.Concurrent;
using Mlcp.Application.Onboarding;

namespace Mlcp.Integration.Azure.Regions;

/// <summary>
/// A process-local region directory for local development and tests, where a single region is
/// assumed (ADR-021). Not for deployment: it is not shared between instances or regions.
/// </summary>
public sealed class InMemoryRegionDirectory : IRegionDirectory
{
    private readonly ConcurrentDictionary<Guid, string> _regions = new();

    public Task<string?> GetRegionAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(_regions.TryGetValue(tenantId, out var region) ? region : null);

    public Task<RegionRegistration> TryRegisterAsync(Guid tenantId, string region, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        var added = _regions.TryAdd(tenantId, region);
        return Task.FromResult(new RegionRegistration(_regions[tenantId], added));
    }
}
