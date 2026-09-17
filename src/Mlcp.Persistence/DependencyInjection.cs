using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mlcp.Application.Onboarding;
using Mlcp.Application.Sync;
using Mlcp.Persistence.Interceptors;
using Mlcp.Persistence.Stores;
using Mlcp.Persistence.Sync;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the tenant-scoped database context used by request handling and by sync jobs
    /// that operate on one tenant, together with the stores built on it.
    /// </summary>
    /// <remarks>
    /// The context and its interceptor are both scoped, and both read the same scoped
    /// <see cref="ITenantContext"/>. Keeping their lifetimes identical is what guarantees that
    /// the tenant stamped into <c>SESSION_CONTEXT</c> is the same tenant the query filter
    /// applies — two isolation layers agreeing rather than two layers guessing.
    /// </remarks>
    public static IServiceCollection AddMlcpPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddScoped<TenantSessionInterceptor>();

        services.AddDbContext<MlcpDbContext>((serviceProvider, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(MlcpDbContext).Assembly.FullName);

                // Azure SQL drops idle connections and fails over between replicas; without
                // this a routine platform event surfaces to a user as an error page.
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            });

            options.AddInterceptors(serviceProvider.GetRequiredService<TenantSessionInterceptor>());
        });

        services.AddScoped<IOnboardingRepository, OnboardingRepository>();
        services.AddScoped<ITenantOnboardingStore, TenantOnboardingStore>();

        // AddScoped (not TryAdd) so it replaces the web host's "unknown" default.
        services.AddScoped<ITenantDirectoryInfo, TenantDirectoryInfoStore>();
        services.AddScoped<ISyncRunStore, SyncRunStore>();
        services.AddScoped<IStagingStore, SqlStagingStore>();
        services.AddScoped<ISyncGateOverrideStore, SyncGateOverrideStore>();
        services.AddScoped<ISyncGateContext, SyncGateContext>();

        // There is deliberately no out-of-band re-consent signal any more (ADR-016 rule 2): the
        // service that owns the tenant aggregate flags it on this same scoped context.

        return services;
    }

    /// <summary>
    /// Registers the cross-tenant database access used by the scheduler and the deletion sweep.
    /// Only the sync worker calls this; the web application does not.
    /// </summary>
    public static IServiceCollection AddMlcpSystemPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton<ISystemDbContextFactory>(_ => new SystemDbContextFactory(connectionString));

        return services;
    }
}
