using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Common;
using Mlcp.Persistence;
using Mlcp.Persistence.Rls;
using Mlcp.Persistence.Stores;
using Mlcp.Shared.Tenancy;

namespace Mlcp.UnitTests.Tenancy;

/// <summary>
/// Guards against the way four-layer isolation actually fails in practice: not by someone
/// disabling a layer, but by someone adding a table and only wiring three of them.
/// </summary>
/// <remarks>
/// These read the EF model, which needs no database, so they run on every machine and in every
/// CI job rather than only where Docker is available. A new tenant-scoped entity that nobody
/// added to the security policy fails here, in seconds, instead of shipping unprotected.
/// </remarks>
public class RlsCoverageTests
{
    private static MlcpDbContext CreateContext()
    {
        // Model building does not open a connection; the connection string is never used.
        var options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer("Server=model-only;Database=model-only;Integrated Security=true")
            .Options;

        return new MlcpDbContext(options, FixedTenantContext.System);
    }

    private static List<string> TenantScopedTableNamesInModel()
    {
        using var context = CreateContext();

        return context.Model.GetEntityTypes()
            .Where(e => e.ClrType.IsAssignableTo(typeof(ITenantScoped)) && !e.IsOwned())
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void Every_tenant_scoped_table_in_the_model_is_covered_by_the_security_policy()
    {
        var inModel = TenantScopedTableNamesInModel();
        var inPolicy = TenantRlsScript.TenantScopedTables;

        inPolicy.Should().BeEquivalentTo(
            inModel,
            "a tenant-scoped table missing from the RLS policy is protected by application code alone. "
            + "Add it to TenantRlsScript.TenantScopedTables and write a migration.");
    }

    [Fact]
    public void The_policy_lists_no_table_that_the_model_does_not_have()
    {
        // The other direction: a stale entry means a migration will fail on a table that no
        // longer exists, which is a deployment outage rather than a leak, but still ours.
        TenantRlsScript.TenantScopedTables.Should().BeSubsetOf(TenantScopedTableNamesInModel());
    }

    [Fact]
    public void Organization_is_deliberately_not_tenant_scoped()
    {
        // Organization sits above Tenant and cannot carry a TenantId. Pinned so that its
        // absence from the policy reads as a decision rather than an oversight.
        TenantRlsScript.TenantScopedTables.Should().NotContain("Organization");
        typeof(Mlcp.Domain.Tenancy.Organization).Should().NotBeAssignableTo<ITenantScoped>();
    }

    [Fact]
    public void Every_tenant_scoped_entity_has_a_global_query_filter()
    {
        using var context = CreateContext();

        var missingFilter = context.Model.GetEntityTypes()
            .Where(e => e.ClrType.IsAssignableTo(typeof(ITenantScoped)) && !e.IsOwned())
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name)
            .ToList();

        missingFilter.Should().BeEmpty("isolation layer 2 is applied by convention and must cover every entity");
    }

    [Fact]
    public void The_predicate_applies_both_filter_and_block_to_each_table()
    {
        var sql = TenantRlsScript.CreateSecurityPolicy(TenantRlsScript.TenantScopedTables);

        foreach (var table in TenantRlsScript.TenantScopedTables)
        {
            sql.Should().Contain($"ADD FILTER PREDICATE [dbo].[fn_TenantPredicate]([TenantId]) ON [dbo].[{table}]");

            // BLOCK matters as much as FILTER: without it a cross-tenant insert succeeds and
            // then silently disappears from every read, which is far worse to diagnose.
            sql.Should().Contain($"ADD BLOCK PREDICATE [dbo].[fn_TenantPredicate]([TenantId]) ON [dbo].[{table}]");
        }
    }

    [Fact]
    public void The_predicate_honours_the_system_context_escape_hatch()
    {
        TenantRlsScript.CreatePredicateFunction()
            .Should().Contain("SESSION_CONTEXT(N'IsSystem')");
    }

    [Fact]
    public void Building_a_policy_with_no_tables_is_refused()
    {
        var act = () => TenantRlsScript.CreateSecurityPolicy([]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_latest_snapshot_matches_the_live_table_list()
    {
        // A new tenant-scoped table needs a new dated snapshot and a migration that rebuilds
        // the policy from it. Editing an old snapshot would change what an applied migration does.
        TenantRlsScript.Snapshots.Latest.Should().Equal(TenantRlsScript.TenantScopedTables);
    }

    [Fact]
    public void The_initial_snapshot_is_frozen()
    {
        TenantRlsScript.Snapshots.Initial20260902.Should().Equal(
            "AppUser", "AuditLog", "OnboardingStep", "PendingConsentRequest", "SyncRun", "Tenant", "TenantCapabilityProfile");
    }

    [Fact]
    public void The_original_predicate_is_unchanged_for_the_migration_that_uses_it()
    {
        var sql = TenantRlsScript.CreatePredicateFunction();

        sql.Should().Contain("CAST(SESSION_CONTEXT(N'IsSystem') AS bit) = 1;");
        sql.Should().NotContain("IS_MEMBER");
    }

    [Fact]
    public void The_upgraded_predicate_honours_the_system_flag_only_for_the_system_role()
    {
        var statements = TenantRlsScript.UpgradeToRoleGatedSystemClause(TenantRlsScript.Snapshots.Hardening20260917);

        statements.Should().HaveCount(4);
        statements[0].Should().Be(TenantRlsScript.DropSecurityPolicy(), "the schema-bound policy must go before the function");
        statements[1].Should().Be(TenantRlsScript.DropPredicateFunction());
        statements[2].Should().StartWith("CREATE FUNCTION [dbo].[fn_TenantPredicate]");
        statements[2].Should().Contain(
            "(CAST(SESSION_CONTEXT(N'IsSystem') AS bit) = 1 AND (IS_MEMBER(N'mlcp_system') = 1 OR IS_MEMBER(N'db_owner') = 1))");
        statements[2].Should().Contain("@TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)");
        statements[2].Should().NotContain(
            "OR CAST(SESSION_CONTEXT(N'IsSystem') AS bit) = 1",
            "the flag must never be sufficient on its own");
        statements[3].Should().Be(TenantRlsScript.CreateSecurityPolicy(TenantRlsScript.Snapshots.Hardening20260917));
    }

    [Fact]
    public void Downgrading_restores_the_original_predicate()
    {
        var statements = TenantRlsScript.DowngradeToSessionOnlySystemClause(TenantRlsScript.Snapshots.Hardening20260917);

        statements[2].Should().Be(TenantRlsScript.CreatePredicateFunction());
    }

    [Fact]
    public void The_policy_uses_only_the_tables_it_is_given()
    {
        // Migrations pass a dated snapshot. The helper must not read the live list behind their back.
        var sql = TenantRlsScript.CreateSecurityPolicy(["AppUser"]);

        sql.Should().Contain("ON [dbo].[AppUser]");
        sql.Should().NotContain("[dbo].[Tenant]");
    }

    [Fact]
    public void Filter_and_block_can_use_different_functions()
    {
        // Needed by organisation roll-up (ADR-022): reads widen, writes stay single-tenant.
        var sql = TenantRlsScript.CreateSecurityPolicy(["AppUser"], "fn_Read", "fn_Write");

        sql.Should().Contain("ADD FILTER PREDICATE [dbo].[fn_Read]([TenantId]) ON [dbo].[AppUser]");
        sql.Should().Contain("ADD BLOCK PREDICATE [dbo].[fn_Write]([TenantId]) ON [dbo].[AppUser]");
    }

    [Fact]
    public void Extra_admission_clauses_are_or_ed_in()
    {
        var sql = TenantRlsScript.CreatePredicateFunction(
            "fn_Read",
            [.. TenantRlsScript.CurrentAdmissionClauses, "EXISTS (SELECT 1 FROM dbo.OrganizationViewGrant)"]);

        sql.Should().Contain("OR EXISTS (SELECT 1 FROM dbo.OrganizationViewGrant)");
        sql.Should().Contain("WITH SCHEMABINDING");
    }

    [Theory]
    [InlineData("App]User")]
    [InlineData("AppUser; DROP TABLE x")]
    [InlineData("")]
    public void Table_names_that_are_not_plain_identifiers_are_refused(string table)
    {
        var act = () => TenantRlsScript.CreateSecurityPolicy([table]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tenant_deletion_covers_every_tenant_scoped_table_and_deletes_the_tenant_last()
    {
        // A table the deletion store does not know about would either block the tenant delete
        // or, with a cascade, vanish uncounted from the certificate.
        TenantDeletionStore.DeletionOrder.Should().BeEquivalentTo(TenantRlsScript.TenantScopedTables);
        TenantDeletionStore.DeletionOrder.Should().OnlyHaveUniqueItems();
        TenantDeletionStore.DeletionOrder[^1].Should().Be("Tenant");
    }
}
