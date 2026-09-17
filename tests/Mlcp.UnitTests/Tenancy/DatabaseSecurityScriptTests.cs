using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mlcp.Persistence;
using Mlcp.Persistence.Rls;
using Mlcp.Persistence.Security;
using Mlcp.Shared.Tenancy;

namespace Mlcp.UnitTests.Tenancy;

/// <summary>
/// ADR-019 and ADR-026: the database roles hold each tier to least privilege. These pin the
/// generated SQL. The tenant-isolation suite runs it and impersonates the roles.
/// </summary>
public class DatabaseSecurityScriptTests
{
    private static IReadOnlyList<string> Up()
        => DatabaseSecurityScript.UpSql(
            TenantRlsScript.Snapshots.Hardening20260917,
            DatabaseSecurityScript.GlobalTableSnapshots.Hardening20260917);

    private static string UpText() => string.Join(Environment.NewLine, Up());

    [Fact]
    public void All_three_roles_are_created_idempotently()
    {
        foreach (var role in new[] { "mlcp_web", "mlcp_worker", "mlcp_system" })
        {
            Up().Should().Contain(
                $"IF DATABASE_PRINCIPAL_ID(N'{role}') IS NULL EXEC(N'CREATE ROLE [{role}] AUTHORIZATION [dbo]');");
        }
    }

    [Fact]
    public void The_web_role_may_append_to_the_audit_log_but_never_change_it()
    {
        Up().Should().Contain("GRANT SELECT, INSERT ON [dbo].[AuditLog] TO [mlcp_web];");
        Up().Should().Contain("DENY UPDATE, DELETE ON [dbo].[AuditLog] TO [mlcp_web];");
    }

    [Fact]
    public void The_worker_role_may_delete_audit_rows_but_never_update_them()
    {
        Up().Should().Contain("GRANT SELECT, INSERT, DELETE ON [dbo].[AuditLog] TO [mlcp_worker];");
        Up().Should().Contain("DENY UPDATE ON [dbo].[AuditLog] TO [mlcp_worker];");
    }

    [Fact]
    public void Only_the_worker_role_touches_deletion_certificates()
    {
        Up().Should().Contain("GRANT SELECT, INSERT ON [dbo].[DeletionCertificate] TO [mlcp_worker];");
        Up().Where(s => s.Contains("[DeletionCertificate]", StringComparison.Ordinal))
            .Should().NotContain(s => s.Contains("mlcp_web", StringComparison.Ordinal));
    }

    [Fact]
    public void The_system_role_is_granted_nothing()
    {
        // Membership is all the predicate checks; any grant here would be unexplained privilege.
        Up().Where(s => s.StartsWith("GRANT", StringComparison.Ordinal) || s.StartsWith("DENY", StringComparison.Ordinal))
            .Should().NotContain(s => s.Contains("mlcp_system", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_other_tenant_table_and_every_global_table_gets_ordinary_dml_for_both_tiers()
    {
        var expected = TenantRlsScript.Snapshots.Hardening20260917
            .Concat(DatabaseSecurityScript.GlobalTableSnapshots.Hardening20260917)
            .Where(t => t != "AuditLog");

        foreach (var table in expected)
        {
            Up().Should().Contain($"GRANT SELECT, INSERT, UPDATE, DELETE ON [dbo].[{table}] TO [mlcp_web], [mlcp_worker];");
        }
    }

    [Fact]
    public void Both_tiers_can_read_the_migration_history()
    {
        Up().Should().Contain("GRANT SELECT ON [dbo].[__EFMigrationsHistory] TO [mlcp_web], [mlcp_worker];");
    }

    [Theory]
    [InlineData("CONTROL")]
    [InlineData("ALTER")]
    [InlineData("CREATE TABLE")]
    [InlineData("EXECUTE")]
    [InlineData("IMPERSONATE")]
    [InlineData("ON SCHEMA")]
    [InlineData("ON DATABASE")]
    [InlineData("db_owner")]
    [InlineData("db_ddladmin")]
    [InlineData("db_datawriter")]
    public void No_role_receives_ddl_or_broad_rights(string forbidden)
    {
        Up().Where(s => s.StartsWith("GRANT", StringComparison.Ordinal))
            .Should().NotContain(s => s.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        UpText().Should().NotContain("sp_addrolemember");
    }

    [Fact]
    public void Every_grant_names_a_single_object_in_dbo()
    {
        Up().Where(s => s.StartsWith("GRANT", StringComparison.Ordinal) || s.StartsWith("DENY", StringComparison.Ordinal))
            .Should().OnlyContain(s => s.Contains(" ON [dbo].[", StringComparison.Ordinal));
    }

    [Fact]
    public void The_helper_uses_only_the_tables_it_is_given()
    {
        var statements = DatabaseSecurityScript.UpSql(["AuditLog", "AppUser"], []);

        statements.Should().NotContain(s => s.Contains("[Tenant]", StringComparison.Ordinal));
        statements.Should().Contain(s => s.Contains("[AppUser]", StringComparison.Ordinal));
    }

    [Fact]
    public void A_table_list_without_the_audit_log_is_refused()
    {
        var act = () => DatabaseSecurityScript.UpSql(["AppUser"], []);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("DeletionCertificate")]
    [InlineData("AuditLog")]
    public void Specially_granted_tables_cannot_be_listed_as_global(string table)
    {
        var act = () => DatabaseSecurityScript.UpSql(["AuditLog"], [table]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Down_removes_members_and_drops_every_role()
    {
        var down = string.Join(Environment.NewLine, DatabaseSecurityScript.DownSql());

        foreach (var role in DatabaseRoles.All)
        {
            down.Should().Contain($"DROP ROLE [{role}]");
            down.Should().Contain($"ALTER ROLE [{role}] DROP MEMBER");
        }
    }

    [Fact]
    public void Every_table_in_the_model_is_granted_to_someone()
    {
        // A new table that no role can reach would fail in production only.
        var granted = TenantRlsScript.Snapshots.Latest
            .Concat(DatabaseSecurityScript.GlobalTableSnapshots.Latest)
            .Append(DatabaseSecurityScript.DeletionCertificateTable);

        using var context = new MlcpDbContext(
            new DbContextOptionsBuilder<MlcpDbContext>()
                .UseSqlServer("Server=model-only;Database=model-only;Integrated Security=true")
                .Options,
            FixedTenantContext.System);

        var inModel = context.Model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .Distinct();

        granted.Should().BeEquivalentTo(inModel);
    }
}
