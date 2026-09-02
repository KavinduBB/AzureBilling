using FluentAssertions;
using Mlcp.Shared.Tenancy;

namespace Mlcp.UnitTests.Tenancy;

public class TenantContextTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void An_unbound_context_has_no_tenant()
    {
        var context = new TenantContext();

        context.HasTenant.Should().BeFalse();
        context.TenantId.Should().BeNull();
        context.IsSystem.Should().BeFalse();
    }

    [Fact]
    public void Requiring_a_tenant_from_an_unbound_context_throws()
    {
        // Loud beats silent here: the alternative to throwing is a query that quietly runs with
        // no tenant filter value.
        var act = () => new TenantContext().RequireTenantId();

        act.Should().Throw<InvalidOperationException>().WithMessage("*No tenant is in scope*");
    }

    [Fact]
    public void A_bound_context_reports_its_tenant()
    {
        var context = new TenantContext();
        context.SetTenant(TenantA);

        context.TenantId.Should().Be(TenantA);
        context.RequireTenantId().Should().Be(TenantA);
        context.IsSystem.Should().BeFalse();
    }

    [Fact]
    public void Rebinding_to_a_different_tenant_is_refused()
    {
        // If this were allowed, a DbContext that had already stamped SESSION_CONTEXT would keep
        // the old tenant while the query filter used the new one, and layers 2 and 3 would
        // disagree without anything failing.
        var context = new TenantContext();
        context.SetTenant(TenantA);

        var act = () => context.SetTenant(TenantB);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already bound*");
    }

    [Fact]
    public void Rebinding_to_the_same_tenant_is_also_refused()
    {
        // Idempotence is not the point; a second call means two things believe they own the
        // scope, which is worth surfacing even when the values agree.
        var context = new TenantContext();
        context.SetTenant(TenantA);

        var act = () => context.SetTenant(TenantA);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Escalating_a_bound_tenant_to_system_is_refused()
    {
        var context = new TenantContext();
        context.SetTenant(TenantA);

        var act = context.SetSystem;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_empty_tenant_id_is_refused()
    {
        var act = () => new TenantContext().SetTenant(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_system_context_has_no_tenant_but_is_flagged()
    {
        var context = new TenantContext();
        context.SetSystem();

        context.IsSystem.Should().BeTrue();
        context.TenantId.Should().BeNull();
        context.HasTenant.Should().BeFalse();
    }

    [Fact]
    public void The_shared_system_instance_is_flagged()
    {
        FixedTenantContext.System.IsSystem.Should().BeTrue();
        FixedTenantContext.System.TenantId.Should().BeNull();
    }
}
