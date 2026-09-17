using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Tenancy;
using Mlcp.IntegrationTests.Infrastructure;
using Mlcp.Persistence.Rls;
using Mlcp.Shared.Tenancy;
using Xunit;

namespace Mlcp.IntegrationTests.TenantIsolation;

/// <summary>
/// P0-3's acceptance criterion: a cross-tenant read through any repository or endpoint returns
/// zero rows or 404. Required on every PR touching Persistence, Application or Web.
/// </summary>
/// <remarks>
/// Each test names the layer it is exercising. That matters because the layers are meant to be
/// independent — a test that only proves the EF filter works would still pass if row-level
/// security were silently dropped, which is precisely the regression this suite exists to catch.
/// </remarks>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "TenantIsolation")]
public class CrossTenantAccessTests
{
    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly DateTimeOffset Now = TenantSeed.Now;

    private readonly SqlServerFixture _fixture;

    public CrossTenantAccessTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string> TenantScopedTables()
    {
        var data = new TheoryData<string>();

        foreach (var table in TenantRlsScript.TenantScopedTables)
        {
            data.Add(table);
        }

        return data;
    }

    private async Task SeedBothTenantsAsync()
    {
        await TenantSeed.SeedAsync(_fixture, TenantA);
        await TenantSeed.SeedAsync(_fixture, TenantB);
    }

    [RequiresDockerFact]
    public async Task Layer2_query_filter_hides_another_tenants_rows()
    {
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        var users = await asA.AppUsers.ToListAsync();

        users.Should().OnlyContain(u => u.TenantId == TenantA);
        users.Should().NotBeEmpty("tenant A has its own user and must still see it");
    }

    [RequiresDockerFact]
    public async Task Layer2_targeted_lookup_of_another_tenants_row_finds_nothing()
    {
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        var found = await asA.Tenants.FirstOrDefaultAsync(t => t.TenantId == TenantB);

        found.Should().BeNull();
    }

    [RequiresDockerFact]
    public async Task Layer3_row_level_security_holds_when_the_query_filter_is_bypassed()
    {
        // The regression that matters most: someone writes IgnoreQueryFilters() for a good
        // reason and quietly removes layer 2 for that query. The database must still refuse.
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        var all = await asA.AppUsers.IgnoreQueryFilters().ToListAsync();

        all.Should().OnlyContain(u => u.TenantId == TenantA);
    }

    [RequiresDockerFact]
    public async Task Layer3_row_level_security_holds_for_raw_sql()
    {
        // Raw SQL bypasses every application layer by definition.
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        var visibleTenantIds = await asA.Database
            .SqlQuery<Guid>($"SELECT TenantId AS Value FROM dbo.AppUser")
            .ToListAsync();

        visibleTenantIds.Should().OnlyContain(id => id == TenantA);
    }

    [RequiresDockerTheory]
    [MemberData(nameof(TenantScopedTables))]
    public async Task Layer3_hides_another_tenants_rows_in_every_scoped_table(string table)
    {
        // One case per table in the policy, so a table dropped from the policy (or never added)
        // fails by name. Both counts are checked: B's rows must be invisible to A, and A's own
        // rows must be visible, or the test would pass against an empty table.
        await SeedBothTenantsAsync();

        var sql = $"SELECT COUNT_BIG(*) AS Value FROM [dbo].[{table}] WHERE [TenantId] = @tenantId";

        await using var system = _fixture.CreateContext(FixedTenantContext.System);
        (await CountAsync(system, sql, TenantB)).Should().BeGreaterThan(0, $"the seed must put tenant B rows in {table}");

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));
        (await CountAsync(asA, sql, TenantA)).Should().BeGreaterThan(0, $"tenant A must see its own {table} rows");
        (await CountAsync(asA, sql, TenantB)).Should().Be(0, $"tenant A must not see tenant B's {table} rows");
    }

    [RequiresDockerFact]
    public async Task The_live_policy_has_one_filter_and_one_block_predicate_per_scoped_table()
    {
        // Checks the database itself, not the script that should have built it. A migration that
        // silently dropped or narrowed the policy fails here.
        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        var predicates = await system.Database.SqlQuery<PredicateRow>($"""
            SELECT OBJECT_NAME(pr.target_object_id) AS TableName,
                   pr.predicate_type_desc AS PredicateType,
                   pr.operation_desc AS Operation,
                   pr.predicate_definition AS FunctionName
            FROM sys.security_predicates AS pr
            JOIN sys.security_policies AS po ON po.object_id = pr.object_id
            WHERE po.name = {TenantRlsScript.SecurityPolicyName}
            """).ToListAsync();

        foreach (var table in TenantRlsScript.TenantScopedTables)
        {
            predicates.Where(p => p.TableName == table && p.PredicateType == "FILTER")
                .Should().ContainSingle($"{table} needs exactly one FILTER predicate");

            // A BLOCK predicate with no operation covers AFTER INSERT, AFTER UPDATE, BEFORE
            // UPDATE and BEFORE DELETE; a narrower one would leave a write path open.
            predicates.Where(p => p.TableName == table && p.PredicateType == "BLOCK")
                .Should().ContainSingle($"{table} needs exactly one BLOCK predicate")
                .Which.Operation.Should().BeNull("the BLOCK predicate must cover every write operation");
        }

        predicates.Should().OnlyContain(p => p.FunctionName == "([dbo].[fn_TenantPredicate]([TenantId]))");

        var enabled = await system.Database.SqlQuery<bool>($"""
            SELECT is_enabled AS Value FROM sys.security_policies WHERE name = {TenantRlsScript.SecurityPolicyName}
            """).SingleAsync();

        enabled.Should().BeTrue();
    }

    [RequiresDockerFact]
    public async Task Every_table_with_a_tenant_column_is_covered_by_the_live_policy()
    {
        // The model-level test compares the EF model with a list. This one compares the actual
        // database with the actual policy, so a table added by hand-written SQL is caught too.
        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        var withTenantColumn = await system.Database.SqlQuery<string>($"""
            SELECT t.name AS Value
            FROM sys.tables AS t
            JOIN sys.columns AS c ON c.object_id = t.object_id
            WHERE c.name = 'TenantId' AND t.name <> 'DeletionCertificate'
            """).ToListAsync();

        var protectedTables = await system.Database.SqlQuery<string>($"""
            SELECT DISTINCT OBJECT_NAME(target_object_id) AS Value FROM sys.security_predicates
            """).ToListAsync();

        withTenantColumn.Should().BeEquivalentTo(protectedTables);
    }

    [RequiresDockerFact]
    public async Task The_live_predicate_requires_the_system_role_for_the_system_flag()
    {
        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        var definition = await system.Database.SqlQuery<string>($"""
            SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo.fn_TenantPredicate')) AS Value
            """).SingleAsync();

        definition.Should().Contain("IS_MEMBER(N'mlcp_system')");
    }

    [RequiresDockerFact]
    public async Task Writing_a_row_into_another_tenant_is_refused()
    {
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        asA.AppUsers.Add(AppUser.Create(TenantB, Guid.NewGuid(), "mallory@fabrikam.example", "Mallory", AppRole.Viewer, Now));

        var act = async () => await asA.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owned by tenant*");
    }

    [RequiresDockerFact]
    public async Task The_block_predicate_refuses_a_raw_insert_into_another_tenant()
    {
        // Layer 3 alone: raw SQL, so neither the query filter nor the SaveChanges guard is in play.
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        var act = async () => await asA.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.AuditLog (TenantId, Action, EntityType, OccurredUtc, CorrelationId, Outcome, CreatedUtc, UpdatedUtc)
            VALUES ({TenantB}, 'TenantConnected', 'Tenant', SYSDATETIMEOFFSET(), 'forged', 'Succeeded', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
            """);

        (await act.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("block predicate");

        await using var system = _fixture.CreateContext(FixedTenantContext.System);
        (await system.AuditLogs.CountAsync(a => a.CorrelationId == "forged")).Should().Be(0);
    }

    [RequiresDockerFact]
    public async Task The_block_predicate_refuses_moving_a_row_into_another_tenant()
    {
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        var act = async () => await asA.Database.ExecuteSqlAsync(
            $"UPDATE dbo.AppUser SET TenantId = {TenantB} WHERE TenantId = {TenantA}");

        (await act.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("block predicate");

        await using var fresh = _fixture.CreateContext(FixedTenantContext.For(TenantA));
        (await fresh.AppUsers.CountAsync()).Should().BeGreaterThan(0, "tenant A's user must still be tenant A's");
    }

    [RequiresDockerFact]
    public async Task A_raw_update_aimed_at_another_tenant_changes_nothing()
    {
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        // The FILTER predicate makes B's rows invisible to the UPDATE, so it matches nothing.
        var affected = await asA.Database.ExecuteSqlAsync(
            $"UPDATE dbo.AppUser SET DisplayName = 'tampered' WHERE TenantId = {TenantB}");

        affected.Should().Be(0);

        await using var asB = _fixture.CreateContext(FixedTenantContext.For(TenantB));
        var users = await asB.AppUsers.ToListAsync();
        users.Should().NotBeEmpty().And.OnlyContain(u => u.DisplayName != "tampered");
    }

    [RequiresDockerFact]
    public async Task An_unbound_context_reads_nothing()
    {
        await SeedBothTenantsAsync();

        await using var unbound = _fixture.CreateContext(new TenantContext());

        var users = await unbound.AppUsers.IgnoreQueryFilters().ToListAsync();

        users.Should().BeEmpty("an unresolved tenant must degrade to no data, never to all data");
    }

    [RequiresDockerFact]
    public async Task A_connection_that_was_never_stamped_reads_nothing_even_with_filters_off()
    {
        // Isolates layer 3 completely: the EF filter is pointed at tenant A, but SESSION_CONTEXT
        // is empty, so the database predicate matches no rows.
        await SeedBothTenantsAsync();

        await using var unstamped = _fixture.CreateContextWithoutSessionStamp(FixedTenantContext.For(TenantA));

        var users = await unstamped.AppUsers.IgnoreQueryFilters().ToListAsync();

        users.Should().BeEmpty();
    }

    [RequiresDockerFact]
    public async Task The_system_context_sees_every_tenant()
    {
        await SeedBothTenantsAsync();

        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        var tenantIds = await system.AppUsers.Select(u => u.TenantId).Distinct().ToListAsync();

        tenantIds.Should().Contain([TenantA, TenantB]);
    }

    [RequiresDockerFact]
    public async Task Session_context_cannot_be_widened_after_it_is_stamped()
    {
        // The stamp is written read-only, so injected or stray SQL cannot escalate a session to
        // another tenant part-way through a request.
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        var act = async () => await asA.Database.ExecuteSqlAsync(
            $"EXEC sp_set_session_context @key = N'IsSystem', @value = 1");

        await act.Should().ThrowAsync<SqlException>("the session context key is set read-only");
    }

    [RequiresDockerFact]
    public async Task A_pooled_connection_reused_by_another_tenant_carries_no_stamp_over()
    {
        // The interceptor's safety argument rests on sp_reset_connection clearing SESSION_CONTEXT
        // when a pooled connection is reused. A pool of one guarantees the same physical session
        // serves both tenants, and the SPID check proves it did.
        await SeedBothTenantsAsync();

        var pooled = _fixture.SingleConnectionPool($"MlcpPoolReuse-{Guid.NewGuid():N}");

        short spidAsA;

        await using (var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA), pooled))
        {
            spidAsA = await asA.Database.SqlQuery<short>($"SELECT @@SPID AS Value").SingleAsync();
            (await asA.AppUsers.IgnoreQueryFilters().CountAsync(u => u.TenantId == TenantA)).Should().BeGreaterThan(0);
        }

        await using var asB = _fixture.CreateContext(FixedTenantContext.For(TenantB), pooled);

        var spidAsB = await asB.Database.SqlQuery<short>($"SELECT @@SPID AS Value").SingleAsync();
        spidAsB.Should().Be(spidAsA, "the test is only meaningful if the pool reused the session");

        var stamped = await asB.Database
            .SqlQuery<Guid>($"SELECT CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier) AS Value")
            .SingleAsync();
        stamped.Should().Be(TenantB);

        (await asB.AppUsers.IgnoreQueryFilters().CountAsync(u => u.TenantId == TenantA)).Should().Be(0);
        (await asB.AppUsers.IgnoreQueryFilters().CountAsync(u => u.TenantId == TenantB)).Should().BeGreaterThan(0);
    }

    private static async Task<long> CountAsync(Mlcp.Persistence.MlcpDbContext context, string sql, Guid tenantId)
        => await context.Database
            .SqlQueryRaw<long>(sql, new SqlParameter("@tenantId", tenantId))
            .SingleAsync();

    private sealed class PredicateRow
    {
        public string TableName { get; set; } = string.Empty;

        public string PredicateType { get; set; } = string.Empty;

        public string? Operation { get; set; }

        public string FunctionName { get; set; } = string.Empty;
    }
}
