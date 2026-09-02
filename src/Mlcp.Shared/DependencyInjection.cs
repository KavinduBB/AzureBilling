using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Shared;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the cross-cutting services every host needs: tenant scope, clock, per-tenant
    /// token acquisition and the Microsoft API resilience pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TenantContext"/> is registered as itself and as <see cref="ITenantContext"/>
    /// resolving to the same scoped instance. The concrete type is what the tenant-resolution
    /// middleware writes to; everything else depends on the read-only interface, so no consumer
    /// can rebind the tenant mid-scope.
    /// </para>
    /// <para>
    /// Lifetimes are deliberate. <see cref="MicrosoftApiPipelines"/> and
    /// <see cref="TenantTokenProvider"/> are singletons because their whole value is the state
    /// they accumulate — circuit-breaker position and cached tokens. <see
    /// cref="MicrosoftApiExecutor"/> is scoped because it depends on
    /// <see cref="ITenantReconsentSignal"/>, whose web implementation is scoped to the request's
    /// tenant; registering it as a singleton would capture that scoped dependency and flag the
    /// wrong tenant.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMlcpShared(
        this IServiceCollection services,
        MlcpIdentityOptions? identityOptions = null,
        MicrosoftApiResilienceOptions? resilienceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.TryAddSingleton(resilienceOptions ?? MicrosoftApiResilienceOptions.Default);
        services.TryAddSingleton<MicrosoftApiPipelines>();
        services.TryAddScoped<MicrosoftApiExecutor>();

        if (identityOptions is not null)
        {
            services.TryAddSingleton(identityOptions);
            services.TryAddSingleton<ITenantTokenProvider, TenantTokenProvider>();
        }

        return services;
    }
}
