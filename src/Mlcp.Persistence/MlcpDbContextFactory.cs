using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> to build the model for migrations.
/// </summary>
/// <remarks>
/// <para>
/// Migrations describe schema, never data, so the tenant this context is bound to is
/// irrelevant — but <see cref="MlcpDbContext"/> requires one. It is given the system context,
/// which is the only context with no tenant and no ability to be confused for a real one.
/// </para>
/// <para>
/// The connection string comes from <c>MLCP_MIGRATIONS_CONNECTION</c>. Without it, the
/// factory falls back to Windows authentication against a local default instance. That is
/// enough for <c>migrations add</c>, which never connects, and for a developer's own SQL Server.
/// There is deliberately no fallback with a password in it (CLAUDE.md rule 4). A password in
/// source is a password in every clone, and would teach the next person that doing so is fine.
/// </para>
/// </remarks>
public sealed class MlcpDbContextFactory : IDesignTimeDbContextFactory<MlcpDbContext>
{
    public const string DesignTimeConnectionVariable = "MLCP_MIGRATIONS_CONNECTION";

    /// <summary>Password-free fallback: integrated security against the local default instance.</summary>
    public const string PasswordlessFallbackConnection =
        "Server=localhost;Database=Mlcp;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=True";

    public MlcpDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer(ResolveConnectionString(), sql => sql.MigrationsAssembly(typeof(MlcpDbContext).Assembly.FullName))
            .Options;

        return new MlcpDbContext(options, FixedTenantContext.System);
    }

    /// <summary>
    /// Returns <c>MLCP_MIGRATIONS_CONNECTION</c>, or the password-free fallback when it is unset.
    /// </summary>
    public static string ResolveConnectionString()
        => ResolveConnectionString(Environment.GetEnvironmentVariable(DesignTimeConnectionVariable));

    /// <summary>Returns <paramref name="configured"/>, or the password-free fallback when it is blank.</summary>
    public static string ResolveConnectionString(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return PasswordlessFallbackConnection;
        }

        return configured;
    }
}
