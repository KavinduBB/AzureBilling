using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mlcp.Shared.Resilience;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Shared;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the cross-cutting services every host needs: tenant scope, clock and the
    /// Microsoft API resilience pipeline.
    /// </summary>
    /// <remarks>
    /// <see cref="TenantContext"/> is registered as itself and as
    /// <see cref="ITenantContext"/> resolving to the same scoped instance. The concrete type is
    /// what the tenant-resolution middleware writes to; everything else depends on the
    /// read-only interface, so no consumer can rebind the tenant mid-scope.
    /// </remarks>
    public static IServiceCollection AddMlcpShared(
        this IServiceCollection services,
        MicrosoftApiResilienceOptions? resilienceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.TryAddSingleton(resilienceOptions ?? MicrosoftApiResilienceOptions.Default);
        services.TryAddSingleton<MicrosoftApiPipelines>();
        services.TryAddSingleton<MicrosoftApiExecutor>();

        return services;
    }
}
