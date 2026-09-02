using Microsoft.EntityFrameworkCore;
using Mlcp.Persistence.Interceptors;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Persistence;

/// <summary>
/// Creates a <see cref="MlcpDbContext"/> bound to the system context, which sees every tenant.
/// </summary>
/// <remarks>
/// <para>
/// A handful of operations are legitimately cross-tenant: the sync scheduler choosing which
/// tenants are due, the deletion sweep, and flagging a tenant whose credential Microsoft has
/// rejected. They cannot run under a request-scoped context bound to one customer.
/// </para>
/// <para>
/// This is a deliberately small and deliberately awkward door. It is a separate type with its
/// own options and its own interceptor rather than a flag on the normal context, so that every
/// use of it is visible in a constructor signature and shows up in review. The web application
/// does not register it.
/// </para>
/// </remarks>
public interface ISystemDbContextFactory
{
    MlcpDbContext CreateDbContext();
}

/// <inheritdoc cref="ISystemDbContextFactory"/>
public sealed class SystemDbContextFactory : ISystemDbContextFactory
{
    private readonly DbContextOptions<MlcpDbContext> _options;

    public SystemDbContextFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer(connectionString)

            // The interceptor is bound to the system context here, not resolved from the
            // container, so this factory cannot accidentally stamp a request's tenant into
            // SESSION_CONTEXT while the query filter is running wide open.
            .AddInterceptors(new TenantSessionInterceptor(FixedTenantContext.System))
            .Options;
    }

    public MlcpDbContext CreateDbContext() => new(_options, FixedTenantContext.System);
}
