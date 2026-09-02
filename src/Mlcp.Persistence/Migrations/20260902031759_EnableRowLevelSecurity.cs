using Microsoft.EntityFrameworkCore.Migrations;
using Mlcp.Persistence.Rls;

#nullable disable

namespace Mlcp.Persistence.Migrations;

/// <summary>
/// Adds isolation layer 3: a SQL Server security policy that filters and blocks by tenant on
/// every tenant-scoped table (docs/03-architecture.md §5.1, docs/04-data-model.md §10).
/// </summary>
/// <remarks>
/// The SQL is produced by <see cref="TenantRlsScript"/> rather than pasted here so that the
/// list of covered tables is a single reviewable value, and so the tenant-isolation test suite
/// can assert that list against the EF model. A new tenant-scoped table that nobody adds to
/// <see cref="TenantRlsScript.TenantScopedTables"/> fails CI instead of shipping unprotected.
/// </remarks>
public partial class EnableRowLevelSecurity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(TenantRlsScript.DropSecurityPolicy());
        migrationBuilder.Sql(TenantRlsScript.DropPredicateFunction());
        migrationBuilder.Sql(TenantRlsScript.CreatePredicateFunction());
        migrationBuilder.Sql(TenantRlsScript.CreateSecurityPolicy(TenantRlsScript.TenantScopedTables));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Order matters: the function is schema-bound by the policy and cannot drop first.
        migrationBuilder.Sql(TenantRlsScript.DropSecurityPolicy());
        migrationBuilder.Sql(TenantRlsScript.DropPredicateFunction());
    }
}
