using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Common;
using Mlcp.Persistence;
using Mlcp.Persistence.Rls;
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
}
