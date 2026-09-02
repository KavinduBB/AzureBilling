using Microsoft.Extensions.DependencyInjection;
using Mlcp.Application.Onboarding;
using Mlcp.Application.Sync;

namespace Mlcp.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the use cases. Every one of these depends only on abstractions declared in this
    /// assembly, so the composition root chooses the persistence and integration behind them.
    /// </summary>
    public static IServiceCollection AddMlcpApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<OnboardingService>();
        services.AddScoped<CapabilityDiscoveryService>();
        services.AddScoped<SyncPipeline>();

        return services;
    }
}
