using Microsoft.EntityFrameworkCore;
using Mlcp.Persistence;
using Mlcp.Persistence.Interceptors;
using Mlcp.Shared.Tenancy;
using Testcontainers.MsSql;
using Xunit;

namespace Mlcp.IntegrationTests.Infrastructure;

/// <summary>
/// A real SQL Server in a container, migrated to head.
/// </summary>
/// <remarks>
/// Row-level security cannot be tested against an in-memory or SQLite provider — the security
/// policy, the predicate function and <c>SESSION_CONTEXT</c> only exist in SQL Server. Testing
/// isolation against a fake provider would verify the EF query filter and nothing else, which
/// is the layer least likely to be the one that fails.
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        DockerAvailability.ThrowIfRequiredButMissing();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        if (DockerAvailability.ExternalSqlConnectionString is { } external)
        {
            ConnectionString = external;

            await using var externalContext = CreateContext(FixedTenantContext.System);
            await externalContext.Database.EnsureDeletedAsync();
            await externalContext.Database.MigrateAsync();
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // Migrations run under the system context: the schema, the predicate function and the
        // security policy are all cross-tenant objects.
        await using var context = CreateContext(FixedTenantContext.System);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Builds a context bound to <paramref name="tenantContext"/>, wired exactly as the
    /// application wires it, including the session interceptor that feeds RLS.
    /// </summary>
    public MlcpDbContext CreateContext(ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        var options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(new TenantSessionInterceptor(tenantContext))
            .Options;

        return new MlcpDbContext(options, tenantContext);
    }

    /// <summary>
    /// A context whose EF filter is bound to <paramref name="tenantContext"/> but whose
    /// connection is <em>not</em> stamped, used to prove that an unstamped session sees nothing.
    /// </summary>
    public MlcpDbContext CreateContextWithoutSessionStamp(ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        var options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new MlcpDbContext(options, tenantContext);
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
