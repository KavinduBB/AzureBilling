using Mlcp.Application.Sync;
using Mlcp.Persistence.Sync;

namespace Mlcp.Sync.Jobs;

public static class SyncMaintenanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the stuck-run and staging sweepers (ADR-024 §7–8).
    /// </summary>
    /// <remarks>
    /// Requires <c>AddMlcpSystemPersistence</c> first: both sweeps read every tenant's runs
    /// through the system context factory.
    /// </remarks>
    public static IServiceCollection AddSyncMaintenance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISyncMaintenanceStore, SyncMaintenanceStore>();
        services.AddHostedService<StuckRunSweeper>();
        services.AddHostedService<StagingSweeper>();

        return services;
    }
}
