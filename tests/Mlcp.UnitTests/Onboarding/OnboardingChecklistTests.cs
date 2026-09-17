using FluentAssertions;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Onboarding;

/// <summary>
/// P0-9: the checklist reflects the capability profile, including the difference between
/// "you can fix this" and "this can never be unlocked for your agreement".
/// </summary>
public class OnboardingChecklistTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private static Tenant TenantWith(AgreementType agreement, bool partnerManaged = false)
    {
        var tenant = Tenant.Register(Guid.NewGuid(), "Contoso", "contoso.example", "westeurope", Now);
        tenant.ConfirmConsent(Guid.NewGuid(), Now);
        tenant.SetAgreementType(agreement, Now);
        tenant.SetPartnerManagement(partnerManaged, partnerManaged ? Guid.NewGuid() : null, Now);
        return tenant;
    }

    private static TenantCapabilityProfile ProfileFrom(
        Guid tenantId,
        GraphProbeResult graph,
        ArmProbeResult arm,
        BillingProbeResult billing)
    {
        var profile = TenantCapabilityProfile.Undiscovered(tenantId, Now);
        var resolution = CapabilityResolver.Resolve(graph, arm, billing);

        foreach (var (capability, unavailable) in resolution.Capabilities)
        {
            if (unavailable is null)
            {
                profile.MarkAvailable(capability, Now);
            }
            else
            {
                profile.MarkUnavailable(unavailable, Now);
            }
        }

        return profile;
    }

    [Fact]
    public void The_checklist_always_shows_the_three_authorisation_systems_separately()
    {
        // Collapsing them into one "connect" step is the most common way a customer believes
        // they have finished when they have not: consent grants nothing on Azure, and an Azure
        // role grants nothing on billing.
        var tenant = TenantWith(AgreementType.Mca);
        var profile = TenantCapabilityProfile.Undiscovered(tenant.TenantId, Now);

        var checklist = OnboardingChecklist.Build(tenant, profile);

        checklist.Items.Select(i => i.Key).Should().BeEquivalentTo(
            ["graph-consent", "azure-rbac", "billing-role", "usage-reports"]);
    }

    [Fact]
    public void A_fully_granted_mca_tenant_has_nothing_outstanding()
    {
        var tenant = TenantWith(AgreementType.Mca);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, true),
            new ArmProbeResult([new AzureSubscriptionProbe(Guid.NewGuid(), "Prod", AgreementType.Mca)], true),
            new BillingProbeResult([new BillingAccountProbe("ba", AgreementType.Mca)], true, true));

        var checklist = OnboardingChecklist.Build(tenant, profile);

        checklist.CompletedCount.Should().Be(4);
        checklist.OutstandingActions.Should().BeEmpty();
    }

    [Fact]
    public void A_mosa_tenant_is_explained_rather_than_prompted()
    {
        // There is no billing role a MOSA customer can grant. Showing them a "fix this" button
        // would send them to a portal screen that cannot help.
        var tenant = TenantWith(AgreementType.Mosa);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, false),
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

        var checklist = OnboardingChecklist.Build(tenant, profile);

        var prices = checklist.Items.Single(i => i.Key == "billing-role");
        prices.IsComplete.Should().BeFalse();
        prices.IsActionable.Should().BeFalse();
        checklist.Explanations.Should().Contain(prices);
        checklist.OutstandingActions.Should().NotContain(prices);
    }

    [Fact]
    public void A_mosa_tenant_is_still_prompted_for_the_things_it_can_change()
    {
        // Being on MOSA does not stop them granting usage consent or an Azure role.
        var tenant = TenantWith(AgreementType.Mosa);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, false),
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

        var checklist = OnboardingChecklist.Build(tenant, profile);

        checklist.OutstandingActions.Select(i => i.Key).Should().Contain("usage-reports");
        checklist.OutstandingActions.Select(i => i.Key).Should().Contain("azure-rbac");
    }

    [Fact]
    public void A_csp_managed_tenant_is_pointed_at_the_partner_guide()
    {
        var tenant = TenantWith(AgreementType.CspManaged, partnerManaged: true);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, false, OwnerTenantId: Guid.NewGuid()),
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

        var checklist = OnboardingChecklist.Build(tenant, profile);

        checklist.IsPartnerManaged.Should().BeTrue();
        checklist.Items.Single(i => i.Key == "billing-role").Guide
            .Should().Be(RemediationGuide.PartnerManaged);
    }

    [Fact]
    public void An_mca_tenant_missing_only_a_billing_role_gets_an_actionable_prompt()
    {
        var tenant = TenantWith(AgreementType.Mca);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, true),
            new ArmProbeResult([new AzureSubscriptionProbe(Guid.NewGuid(), "Prod", AgreementType.Mca)], true),
            new BillingProbeResult([new BillingAccountProbe("ba", AgreementType.Mca)], false, false));

        var checklist = OnboardingChecklist.Build(tenant, profile);

        var prices = checklist.Items.Single(i => i.Key == "billing-role");
        prices.IsActionable.Should().BeTrue();
        prices.Guide.Should().Be(RemediationGuide.BillingRole);
        checklist.OutstandingActions.Should().Contain(prices);
    }

    [Theory]
    [InlineData(CapabilityUnavailableReason.NoBillingAccount, RemediationGuide.None)]
    [InlineData(CapabilityUnavailableReason.NotMca, RemediationGuide.None)]
    [InlineData(CapabilityUnavailableReason.BillingRoleMissing, RemediationGuide.BillingRole)]
    public void An_undetermined_tenant_is_always_offered_guide_c(CapabilityUnavailableReason reason, RemediationGuide probeGuide)
    {
        // ADR-020: Guide C is the one step that reveals the agreement, so it must never be hidden
        // behind a guess.
        var tenant = TenantWith(AgreementType.Undetermined);
        var profile = TenantCapabilityProfile.Undiscovered(tenant.TenantId, Now);
        profile.MarkUnavailable(new CapabilityUnavailable(Capability.BillingTransactions, reason, probeGuide), Now);

        var checklist = OnboardingChecklist.Build(tenant, profile);

        var prices = checklist.Items.Single(i => i.Key == "billing-role");
        prices.IsActionable.Should().BeTrue();
        prices.Guide.Should().Be(RemediationGuide.BillingRole);
        prices.Detail.Should().Be(OnboardingChecklist.UndeterminedBillingCopy);
        prices.Unavailable!.Reason.Should().Be(reason, "the typed reason is kept for metrics");
        checklist.OutstandingActions.Should().Contain(prices);
        checklist.OffersManualPricing.Should().BeTrue();
    }

    [Fact]
    public void An_undetermined_tenant_keeps_guide_b_actionable()
    {
        var tenant = TenantWith(AgreementType.Undetermined);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, false),
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

        var checklist = OnboardingChecklist.Build(tenant, profile);

        checklist.OutstandingActions.Select(i => i.Key).Should().Contain(["azure-rbac", "billing-role"]);
    }

    [Fact]
    public void A_mosa_tenant_is_not_given_the_undetermined_prompt()
    {
        var tenant = TenantWith(AgreementType.Mosa);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, false),
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

        var checklist = OnboardingChecklist.Build(tenant, profile);

        checklist.Items.Single(i => i.Key == "billing-role").Detail
            .Should().NotBe(OnboardingChecklist.UndeterminedBillingCopy);
    }

    [Fact]
    public void Manual_pricing_is_not_offered_to_an_mca_tenant_with_billing_access()
    {
        var tenant = TenantWith(AgreementType.Mca);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, true),
            new ArmProbeResult([new AzureSubscriptionProbe(Guid.NewGuid(), "Prod", AgreementType.Mca)], true),
            new BillingProbeResult([new BillingAccountProbe("ba", AgreementType.Mca)], true, true));

        OnboardingChecklist.Build(tenant, profile).OffersManualPricing.Should().BeFalse();
    }

    [Fact]
    public void Every_incomplete_item_carries_something_to_show_the_user()
    {
        // An incomplete row with no reason and no guide would render as a blank panel, which
        // CLAUDE.md rule 9 calls a bug.
        var tenant = TenantWith(AgreementType.Ea);

        var profile = ProfileFrom(
            tenant.TenantId,
            new GraphProbeResult(true, true, false),
            new ArmProbeResult([new AzureSubscriptionProbe(Guid.NewGuid(), "Ent", AgreementType.Ea)], false),
            new BillingProbeResult([new BillingAccountProbe("ea", AgreementType.Ea)], true, false));

        var checklist = OnboardingChecklist.Build(tenant, profile);

        foreach (var item in checklist.Items.Where(i => !i.IsComplete))
        {
            item.Unavailable.Should().NotBeNull();
            item.Detail.Should().NotBeNullOrWhiteSpace($"'{item.Key}' must explain itself");
        }
    }
}
