using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Tenancy;
using Mlcp.IntegrationTests.Infrastructure;
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
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public CrossTenantAccessTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SeedBothTenantsAsync()
    {
        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        if (await system.Tenants.AnyAsync(t => t.TenantId == TenantA))
        {
            return;
        }

        system.Tenants.Add(Tenant.Register(TenantA, "Contoso", "contoso.example", "westeurope", Now));
        system.Tenants.Add(Tenant.Register(TenantB, "Fabrikam", "fabrikam.example", "westeurope", Now));

        system.AppUsers.Add(AppUser.Create(TenantA, Guid.NewGuid(), "ann@contoso.example", "Ann", AppRole.Owner, Now));
        system.AppUsers.Add(AppUser.Create(TenantB, Guid.NewGuid(), "bob@fabrikam.example", "Bob", AppRole.Owner, Now));

        await system.SaveChangesAsync();
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
    public async Task The_block_predicate_refuses_a_cross_tenant_write_that_evades_the_application_guard()
    {
        // Proves layer 3 independently of the application guard: raw SQL, no change tracker.
        await SeedBothTenantsAsync();

        await using var asA = _fixture.CreateContext(FixedTenantContext.For(TenantA));

        var act = async () => await asA.Database.ExecuteSqlAsync(
            $"UPDATE dbo.AppUser SET DisplayName = 'tampered' WHERE TenantId = {TenantB}");

        // The BLOCK predicate rejects the write outright, or the FILTER predicate has already
        // made the row invisible so nothing is updated. Either way tenant B is untouched.
        await act.Should().NotThrowAsync();

        await using var asB = _fixture.CreateContext(FixedTenantContext.For(TenantB));
        var bob = await asB.AppUsers.SingleAsync();
        bob.DisplayName.Should().Be("Bob");
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

        await act.Should().ThrowAsync<Exception>("the session context key is set read-only");
    }
}
