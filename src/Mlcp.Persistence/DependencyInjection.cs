using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mlcp.Persistence.Interceptors;
using Mlcp.Shared.Resilience;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the tenant-scoped database context used by request handling and by sync jobs
    /// that operate on one tenant.
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
                sql.EnableRetryOnFailure();
            });

            options.AddInterceptors(serviceProvider.GetRequiredService<TenantSessionInterceptor>());
        });

        return services;
    }

    /// <summary>
    /// Registers the cross-tenant database access used by the scheduler, the deletion sweep and
    /// the re-consent signal. Only the sync worker calls this; the web application does not.
    /// </summary>
    public static IServiceCollection AddMlcpSystemPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton<ISystemDbContextFactory>(_ => new SystemDbContextFactory(connectionString));
        services.AddSingleton<ITenantReconsentSignal, TenantReconsentSignal>();

        return services;
    }
}
