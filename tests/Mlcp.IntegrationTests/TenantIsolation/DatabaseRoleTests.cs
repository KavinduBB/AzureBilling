using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Audit;
using Mlcp.IntegrationTests.Infrastructure;
using Mlcp.Shared.Tenancy;
using Xunit;

namespace Mlcp.IntegrationTests.TenantIsolation;

/// <summary>
/// ADR-019 and ADR-026, enforced by the database: the web role cannot rewrite history or
/// bypass row-level security, and only the worker's system role honours the system flag.
/// </summary>
/// <remarks>
/// Every test impersonates a user created without a login (<c>EXECUTE AS USER</c>), because
/// the fixture's own sysadmin connection is <c>dbo</c> and bypasses permission checks. Each
/// denial is paired with a positive control, so a broken setup that denies everything cannot
/// pass as a working one.
/// </remarks>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "TenantIsolation")]
public class DatabaseRoleTests
{
    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private readonly SqlServerFixture _fixture;

    public DatabaseRoleTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SeedBothTenantsAsync()
    {
        await TenantSeed.SeedAsync(_fixture, TenantA);
        await TenantSeed.SeedAsync(_fixture, TenantB);
    }

    [RequiresDockerFact]
    public async Task The_web_role_can_append_and_read_its_own_audit_rows()
    {
        await SeedBothTenantsAsync();

        await using var web = _fixture.CreateContextAs(FixedTenantContext.For(TenantA), SqlServerFixture.WebUser);

        web.AuditLogs.Add(AuditLog.ForUser(
            TenantA, Guid.NewGuid(), "ann@contoso.example", AuditAction.ConsentRequested, "Tenant", null,
            AuditOutcome.Succeeded, "web-append", TenantSeed.Now));
        await web.SaveChangesAsync();

        (await web.AuditLogs.CountAsync(a => a.CorrelationId == "web-append")).Should().Be(1);
    }

    [RequiresDockerFact]
    public async Task The_web_role_cannot_update_an_audit_row()
    {
        await SeedBothTenantsAsync();

        await using var web = _fixture.CreateContextAs(FixedTenantContext.For(TenantA), SqlServerFixture.WebUser);

        var act = async () => await web.Database.ExecuteSqlAsync(
            $"UPDATE dbo.AuditLog SET Outcome = 'Failed' WHERE TenantId = {TenantA}");

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(229, "229 is 'permission denied'");
    }

    [RequiresDockerFact]
    public async Task The_web_role_cannot_delete_an_audit_row()
    {
        await SeedBothTenantsAsync();

        await using var web = _fixture.CreateContextAs(FixedTenantContext.For(TenantA), SqlServerFixture.WebUser);

        var act = async () => await web.Database.ExecuteSqlAsync(
            $"DELETE FROM dbo.AuditLog WHERE TenantId = {TenantA}");

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(229);

        await using var system = _fixture.CreateContext(FixedTenantContext.System);
        (await system.AuditLogs.CountAsync(a => a.TenantId == TenantA)).Should().BeGreaterThan(0);
    }

    [RequiresDockerFact]
    public async Task The_worker_role_cannot_update_an_audit_row_either()
    {
        // The worker may delete audit rows (tenant deletion only) but never rewrite them.
        await SeedBothTenantsAsync();

        await using var worker = _fixture.CreateContextAs(FixedTenantContext.System, SqlServerFixture.WorkerUser);

        var act = async () => await worker.Database.ExecuteSqlAsync(
            $"UPDATE dbo.AuditLog SET Detail = 'rewritten' WHERE TenantId = {TenantA}");

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(229);
    }

    [RequiresDockerFact]
    public async Task The_web_role_cannot_read_deletion_certificates()
    {
        await using var web = _fixture.CreateContextAs(FixedTenantContext.For(TenantA), SqlServerFixture.WebUser);

        var act = async () => await web.DeletionCertificates.CountAsync();

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(229);
    }

    [RequiresDockerFact]
    public async Task The_web_role_cannot_change_the_schema_or_the_security_policy()
    {
        await using var web = _fixture.CreateContextAs(FixedTenantContext.For(TenantA), SqlServerFixture.WebUser);

        // Both attempts run inside a transaction that is always rolled back. If a regression ever
        // granted the right, the policy must not stay switched off for the rest of the suite.
        var createTable = async () => await web.Database.ExecuteSqlRawAsync("""
            BEGIN TRY
                BEGIN TRANSACTION;
                CREATE TABLE dbo.Intruder (Id int);
                ROLLBACK TRANSACTION;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH
            """);

        var disablePolicy = async () => await web.Database.ExecuteSqlRawAsync("""
            BEGIN TRY
                BEGIN TRANSACTION;
                ALTER SECURITY POLICY dbo.TenantSecurityPolicy WITH (STATE = OFF);
                ROLLBACK TRANSACTION;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH
            """);

        (await createTable.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(262, "262 is 'CREATE permission denied'");
        (await disablePolicy.Should().ThrowAsync<SqlException>()).Which.Number.Should().NotBe(15590);

        var hasAlter = await web.Database
            .SqlQuery<int>($"SELECT HAS_PERMS_BY_NAME(N'dbo.TenantSecurityPolicy', N'OBJECT', N'ALTER') AS Value")
            .SingleAsync();
        hasAlter.Should().Be(0);
    }

    [RequiresDockerFact]
    public async Task A_web_connection_that_sets_the_system_flag_still_sees_no_tenant()
    {
        // ADR-026: the flag alone is not enough. Here the web user carries IsSystem = 1 and no
        // tenant. Under the old predicate it would have read every tenant.
        await SeedBothTenantsAsync();

        await using var web = _fixture.CreateContextAs(new FixedTenantContext(null, isSystem: true), SqlServerFixture.WebUser);

        var flag = await web.Database
            .SqlQuery<bool>($"SELECT CAST(SESSION_CONTEXT(N'IsSystem') AS bit) AS Value")
            .SingleAsync();
        flag.Should().BeTrue("the test must actually carry the system flag");

        (await web.AppUsers.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await web.Tenants.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [RequiresDockerFact]
    public async Task A_web_connection_with_a_tenant_and_the_system_flag_sees_only_that_tenant()
    {
        await SeedBothTenantsAsync();

        await using var web = _fixture.CreateContextAs(new FixedTenantContext(TenantA, isSystem: true), SqlServerFixture.WebUser);

        var tenantIds = await web.AppUsers.IgnoreQueryFilters().Select(u => u.TenantId).Distinct().ToListAsync();

        tenantIds.Should().Equal(TenantA);
    }

    [RequiresDockerFact]
    public async Task A_worker_connection_in_the_system_role_sees_every_tenant()
    {
        await SeedBothTenantsAsync();

        await using var worker = _fixture.CreateContextAs(FixedTenantContext.System, SqlServerFixture.WorkerUser);

        var tenantIds = await worker.AppUsers.Select(u => u.TenantId).Distinct().ToListAsync();

        tenantIds.Should().Contain([TenantA, TenantB]);
    }

    [RequiresDockerFact]
    public async Task A_worker_connection_without_the_system_flag_is_confined_like_any_other()
    {
        await SeedBothTenantsAsync();

        await using var worker = _fixture.CreateContextAs(FixedTenantContext.For(TenantA), SqlServerFixture.WorkerUser);

        var tenantIds = await worker.AppUsers.IgnoreQueryFilters().Select(u => u.TenantId).Distinct().ToListAsync();

        tenantIds.Should().Equal(TenantA);
    }

    [RequiresDockerFact]
    public async Task Both_roles_can_read_the_migration_history()
    {
        foreach (var user in new[] { SqlServerFixture.WebUser, SqlServerFixture.WorkerUser })
        {
            await using var context = _fixture.CreateContextAs(FixedTenantContext.For(TenantA), user);

            var count = await context.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.__EFMigrationsHistory")
                .SingleAsync();

            count.Should().BeGreaterThan(0, $"{user} checks the schema version at startup");
        }
    }
}
