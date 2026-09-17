using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mlcp.Persistence;
using Mlcp.Persistence.Interceptors;
using Mlcp.Persistence.Security;
using Mlcp.Shared.Tenancy;
using Testcontainers.MsSql;
using Xunit;

namespace Mlcp.IntegrationTests.Infrastructure;

/// <summary>
/// A real SQL Server, in a container or an external server, migrated to head.
/// </summary>
/// <remarks>
/// <para>
/// Row-level security cannot be tested against an in-memory or SQLite provider — the security
/// policy, the predicate function and <c>SESSION_CONTEXT</c> only exist in SQL Server. Testing
/// isolation against a fake provider would verify the EF query filter and nothing else, which
/// is the layer least likely to be the one that fails.
/// </para>
/// <para>
/// The fixture connects as a sysadmin, which SQL Server maps to <c>dbo</c>. The deployed web
/// and worker identities are not database owners (ADR-026), so the fixture also creates
/// database users without logins, one per workload role. Tests impersonate them with
/// <c>EXECUTE AS USER</c> through <see cref="CreateContextAs"/>, and so exercise the grants and
/// the role-gated predicate exactly as production sees them.
/// </para>
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>A user in <see cref="DatabaseRoles.Web"/> only, like <c>mlcp_web_user</c>.</summary>
    public const string WebUser = "mlcp_test_web_user";

    /// <summary>A user in <see cref="DatabaseRoles.Worker"/> and <see cref="DatabaseRoles.System"/>, like <c>mlcp_worker_user</c>.</summary>
    public const string WorkerUser = "mlcp_test_worker_user";

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
        }
        else
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();

            // Migrations run under the system context: the schema, the predicate function and
            // the security policy are all cross-tenant objects.
            await using var context = CreateContext(FixedTenantContext.System);
            await context.Database.MigrateAsync();
        }

        await CreateWorkloadUsersAsync();
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
    /// <param name="connectionString">Overrides the fixture's connection string, e.g. to isolate a pool.</param>
    public MlcpDbContext CreateContext(ITenantContext tenantContext, string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        var options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer(connectionString ?? ConnectionString)
            .AddInterceptors(new TenantSessionInterceptor(tenantContext))
            .Options;

        return new MlcpDbContext(options, tenantContext);
    }

    /// <summary>
    /// Builds a context whose connections are stamped for <paramref name="tenantContext"/> and
    /// then run as <paramref name="databaseUser"/> (<c>EXECUTE AS USER ... WITH NO REVERT</c>).
    /// </summary>
    /// <remarks>
    /// Pooling is off for these connections: an impersonation that cannot be reverted must
    /// never be handed back to a pool shared with other tests.
    /// </remarks>
    public MlcpDbContext CreateContextAs(ITenantContext tenantContext, string databaseUser)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseUser);

        var unpooled = new SqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString;

        var options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer(unpooled)
            .AddInterceptors(
                new TenantSessionInterceptor(tenantContext),
                new ImpersonationInterceptor(databaseUser))
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

    /// <summary>A connection string that uses its own, single-connection pool.</summary>
    public string SingleConnectionPool(string applicationName)
        => new SqlConnectionStringBuilder(ConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1,
        }.ConnectionString;

    private async Task CreateWorkloadUsersAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DATABASE_PRINCIPAL_ID(N'{WebUser}') IS NULL CREATE USER [{WebUser}] WITHOUT LOGIN;
            IF DATABASE_PRINCIPAL_ID(N'{WorkerUser}') IS NULL CREATE USER [{WorkerUser}] WITHOUT LOGIN;
            ALTER ROLE [{DatabaseRoles.Web}] ADD MEMBER [{WebUser}];
            ALTER ROLE [{DatabaseRoles.Worker}] ADD MEMBER [{WorkerUser}];
            ALTER ROLE [{DatabaseRoles.System}] ADD MEMBER [{WorkerUser}];
            """;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Switches every opened connection to a database user, after the tenant stamp.</summary>
    private sealed class ImpersonationInterceptor(string databaseUser) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = CreateCommand(connection);
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = CreateCommand(connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private DbCommand CreateCommand(DbConnection connection)
        {
            // NO REVERT is accepted only in an ad hoc batch, so the name cannot be a parameter.
            // It is one of the fixture's own constants; the quote is escaped regardless.
            var command = connection.CreateCommand();
            command.CommandText = $"EXECUTE AS USER = N'{databaseUser.Replace("'", "''", StringComparison.Ordinal)}' WITH NO REVERT;";
            return command;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

/// <summary>
/// Creates cross-tenant stores for tests, as the system context, optionally impersonating a
/// workload user.
/// </summary>
public sealed class TestSystemDbContextFactory(SqlServerFixture fixture, string? databaseUser = null) : ISystemDbContextFactory
{
    public MlcpDbContext CreateDbContext()
        => databaseUser is null
            ? fixture.CreateContext(FixedTenantContext.System)
            : fixture.CreateContextAs(FixedTenantContext.System, databaseUser);
}
