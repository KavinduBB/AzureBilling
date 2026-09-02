using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> to build the model for migrations.
/// </summary>
/// <remarks>
/// Migrations describe schema, never data, so the tenant this context is bound to is
/// irrelevant — but <see cref="MlcpDbContext"/> requires one. It is given the system context,
/// which is the only context with no tenant and no ability to be confused for a real one. The
/// connection string is read from <c>MLCP_MIGRATIONS_CONNECTION</c> when present so a
/// developer can point migrations at a container without editing code.
/// </remarks>
public sealed class MlcpDbContextFactory : IDesignTimeDbContextFactory<MlcpDbContext>
{
    private const string DesignTimeConnectionVariable = "MLCP_MIGRATIONS_CONNECTION";

    private const string DefaultDesignTimeConnection =
        "Server=localhost,1433;Database=Mlcp;User Id=sa;Password=Local_Dev_Password_1;TrustServerCertificate=True;Encrypt=True";

    public MlcpDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(DesignTimeConnectionVariable) ?? DefaultDesignTimeConnection;

        var options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(MlcpDbContext).Assembly.FullName))
            .Options;

        return new MlcpDbContext(options, FixedTenantContext.System);
    }
}
