using FluentAssertions;
using Mlcp.Application.Costs;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Capabilities;

/// <summary>ADR-017: the widest supported scope the app can read, per tenant.</summary>
public class CostScopeResolverTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private const string Profile = "/providers/Microsoft.Billing/billingAccounts/ba/billingProfiles/bp";
    private const string EaAccount = "/providers/Microsoft.Billing/billingAccounts/12345";

    private static SubscriptionCostAccess Sub(AgreementType agreement, bool? readable)
        => new(Guid.NewGuid(), agreement, readable);

    private static CostScopeInput Input(
        AgreementType agreement,
        bool billingRole = false,
        bool billingScopeReadable = false,
        bool rootReadable = false,
        params SubscriptionCostAccess[] subscriptions)
        => new(
            Tenant,
            agreement,
            billingRole,
            billingScopeReadable,
            agreement == AgreementType.Mca ? [Profile] : [],
            agreement == AgreementType.Ea ? EaAccount : null,
            rootReadable,
            subscriptions);

    [Fact]
    public void Rung1_mca_with_a_billing_role_queries_each_billing_profile()
    {
        var plan = CostScopeResolver.Resolve(Input(AgreementType.Mca, billingRole: true, billingScopeReadable: true, rootReadable: true, Sub(AgreementType.Mca, true)));

        plan.Rung.Should().Be(CostQueryScopeRung.BillingScope);
        plan.Scopes.Should().Equal(Profile);
        plan.PurchasesVisible.Should().BeTrue();
    }

    [Fact]
    public void Rung1_ea_with_enterprise_read_queries_the_billing_account()
    {
        var plan = CostScopeResolver.Resolve(Input(AgreementType.Ea, billingRole: true, billingScopeReadable: true));

        plan.Rung.Should().Be(CostQueryScopeRung.BillingScope);
        plan.Scopes.Should().Equal(EaAccount);
    }

    [Fact]
    public void A_billing_role_without_a_readable_billing_scope_is_not_rung1()
    {
        var plan = CostScopeResolver.Resolve(Input(AgreementType.Mca, billingRole: true, billingScopeReadable: false, subscriptions: Sub(AgreementType.Mca, true)));

        plan.Rung.Should().Be(CostQueryScopeRung.PerSubscription);
    }

    [Theory]
    [InlineData(AgreementType.Ea)]
    [InlineData(AgreementType.Mosa)]
    public void Rung2_ea_and_mosp_use_the_root_management_group(AgreementType agreement)
    {
        var plan = CostScopeResolver.Resolve(Input(agreement, rootReadable: true, subscriptions: Sub(agreement, true)));

        plan.Rung.Should().Be(CostQueryScopeRung.RootManagementGroup);
        plan.Scopes.Should().Equal($"/providers/Microsoft.Management/managementGroups/{Tenant:D}");
        plan.PurchasesVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData(AgreementType.Mca)]
    [InlineData(AgreementType.CspManaged)]
    [InlineData(AgreementType.Undetermined)]
    public void Management_groups_are_never_used_outside_ea_and_mosp(AgreementType agreement)
    {
        var plan = CostScopeResolver.Resolve(Input(agreement, rootReadable: true, subscriptions: Sub(AgreementType.Mca, true)));

        plan.Rung.Should().Be(CostQueryScopeRung.PerSubscription);
    }

    [Fact]
    public void An_ea_tenant_with_an_mca_subscription_does_not_use_the_management_group()
    {
        var plan = CostScopeResolver.Resolve(Input(AgreementType.Ea, rootReadable: true, subscriptions: [Sub(AgreementType.Ea, true), Sub(AgreementType.Mca, null)]));

        plan.Rung.Should().Be(CostQueryScopeRung.PerSubscription);
        plan.Scopes.Should().HaveCount(2);
    }

    [Fact]
    public void Rung3_excludes_refused_subscriptions_and_needs_one_readable()
    {
        var readable = Sub(AgreementType.Mca, true);
        var refused = Sub(AgreementType.Mca, false);
        var untried = Sub(AgreementType.Mca, null);

        var plan = CostScopeResolver.Resolve(Input(AgreementType.Mca, subscriptions: [readable, refused, untried]));

        plan.Rung.Should().Be(CostQueryScopeRung.PerSubscription);
        plan.Scopes.Should().Equal(
            CostScopeResolver.SubscriptionScope(readable.SubscriptionId),
            CostScopeResolver.SubscriptionScope(untried.SubscriptionId));
    }

    [Fact]
    public void Nothing_readable_is_rung_none_with_a_reason()
    {
        var plan = CostScopeResolver.Resolve(Input(AgreementType.Mca, subscriptions: Sub(AgreementType.Mca, false)));

        plan.Rung.Should().Be(CostQueryScopeRung.None);
        plan.Scopes.Should().BeEmpty();
        plan.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Large_rung3_tenants_are_nudged_towards_a_billing_role()
    {
        var subscriptions = Enumerable.Range(0, 301).Select(_ => Sub(AgreementType.Mca, true)).ToArray();

        var plan = CostScopeResolver.Resolve(Input(AgreementType.Mca, subscriptions: subscriptions));

        plan.RecommendBillingRoleOrExports.Should().BeTrue();
        plan.Reason.Should().Contain("billing role");
    }
}

/// <summary>The Cost Management QPU quotas: 12/10 s, 60/min, 600/h.</summary>
public class CostQueryQpuBudgetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Twelve_qpu_fit_in_ten_seconds_and_the_thirteenth_waits()
    {
        var budget = new CostQueryQpuBudget();

        for (var i = 0; i < 12; i++)
        {
            budget.TryConsume(1, T0).Should().BeTrue();
        }

        budget.TryConsume(1, T0).Should().BeFalse();
        budget.NextAvailableUtc(1, T0).Should().Be(T0.AddSeconds(10));
        budget.TryConsume(1, T0.AddSeconds(10)).Should().BeTrue();
    }

    [Fact]
    public void The_per_minute_quota_applies_across_ten_second_windows()
    {
        var budget = new CostQueryQpuBudget();

        for (var window = 0; window < 5; window++)
        {
            for (var i = 0; i < 12; i++)
            {
                budget.TryConsume(1, T0.AddSeconds(window * 10)).Should().BeTrue();
            }
        }

        var at = T0.AddSeconds(50);
        budget.TryConsume(1, at).Should().BeFalse("60 QPU were spent inside the last minute");
        budget.NextAvailableUtc(1, at).Should().Be(T0.AddSeconds(60));
    }

    [Fact]
    public void A_query_larger_than_a_quota_is_refused_outright()
    {
        var budget = new CostQueryQpuBudget();

        var act = () => budget.NextAvailableUtc(13, T0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("2026-09-01", "2026-09-17", 1)]
    [InlineData("2026-08-15", "2026-09-17", 2)]
    [InlineData("2025-10-01", "2026-09-30", 12)]
    public void Qpu_is_one_per_month_queried(string from, string to, int expected)
    {
        CostQueryQpuBudget.EstimateQpu(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(expected);
    }
}
