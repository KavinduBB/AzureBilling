using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Shared;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the cross-cutting services every host needs from configuration, and fails at
    /// startup (not at the first Microsoft call) when the app registrations are missing outside
    /// Development.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Reads <c>AzureAd:ClientId</c>, <c>AzureAd:UsageInsightsClientId</c>, <c>Mlcp:KeyVaultUri</c>,
    /// <c>Mlcp:ClientCertificateName</c>, <c>AzureAd:ClientSecret</c>,
    /// <c>AzureAd:UsageInsightsClientSecret</c> and <c>Mlcp:Identity:SendCertificateChain</c>.
    /// </param>
    /// <param name="isDevelopment">True in the Development environment only.</param>
    /// <param name="budget">Interactive for the web host, Worker for the sync host (ADR-016 rule 7).</param>
    /// <exception cref="InvalidOperationException">Outside Development, when the identity configuration is incomplete.</exception>
    public static IServiceCollection AddMlcpShared(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment,
        MicrosoftCallBudget budget)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var identity = MlcpIdentityOptions.FromConfiguration(configuration);

        if (!isDevelopment)
        {
            var problems = identity.ValidateForProduction();

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(
                    "The app registration is not configured: " + string.Join(" ", problems));
            }
        }

        var resilience = MicrosoftApiResilienceOptions.Default with { Budget = budget };

        return services.AddMlcpShared(
            string.IsNullOrWhiteSpace(identity.ClientId) ? null : identity,
            resilience);
    }

    /// <summary>
    /// Registers the cross-cutting services every host needs: tenant scope, clock, per-tenant
    /// token acquisition and the Microsoft API resilience pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TenantContext"/> is registered as itself and as <see cref="ITenantContext"/>
    /// resolving to the same scoped instance. The concrete type is what the tenant-resolution
    /// middleware writes to; everything else depends on the read-only interface.
    /// </para>
    /// <para>
    /// <see cref="MicrosoftApiPipelines"/>, <see cref="MicrosoftApiExecutor"/> and the token
    /// provider are singletons: their value is the state they accumulate (breaker positions,
    /// per-provider concurrency, cached tokens, credential pauses), and none of them touches a
    /// tenant row any more (ADR-016 rule 2).
    /// </para>
    /// <para>
    /// When <paramref name="identityOptions"/> is null an <see cref="UnconfiguredTenantTokenProvider"/>
    /// is registered, so the container validates and every Microsoft call fails with a classified
    /// PlatformCredential reason. Hosts outside Development should use the configuration overload,
    /// which refuses to start instead.
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
        services.TryAddSingleton<MicrosoftApiExecutor>();

        if (identityOptions is not null)
        {
            services.TryAddSingleton(identityOptions);
            services.TryAddSingleton<ITenantTokenProvider, TenantTokenProvider>();
        }
        else
        {
            services.TryAddSingleton<ITenantTokenProvider, UnconfiguredTenantTokenProvider>();
        }

        return services;
    }
}
