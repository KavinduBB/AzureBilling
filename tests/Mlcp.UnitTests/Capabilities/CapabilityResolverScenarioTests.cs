using FluentAssertions;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Capabilities;

/// <summary>
/// P0-7's acceptance criterion: for MOSA, MCA, EA and CSP-managed fixtures, the resolved
/// profile matches the scenario table in docs/01-scope-and-scenarios.md §4.
/// </summary>
/// <remarks>
/// These are the rules most likely to drift as Microsoft changes what it exposes, so each test
/// asserts the reason and not just the boolean. Getting "unavailable" right while getting
/// "why" wrong sends a customer to a remediation guide that cannot help them, which is worse
/// than saying nothing.
/// </remarks>
public class CapabilityResolverScenarioTests
{
    private static readonly Guid PartnerTenant = Guid.Parse("dddddddd-0000-0000-0000-00000000000d");

    private static GraphProbeResult FullyConsentedGraph(Guid? ownerTenantId = null, bool usage = false) =>
        new(
            SubscribedSkusReadable: true,
            DirectorySubscriptionsReadable: true,
            UsageReportsReadable: usage,
            OwnerTenantId: ownerTenantId,
            OrganizationDisplayName: "Contoso",
            DefaultDomain: "contoso.example");

    // Scenario A — web-direct MOSA: licences visible, no billing account anywhere.
    private static CapabilityResolution ResolveMosa() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

    // Scenario B — MCA direct with a billing role granted.
    private static CapabilityResolution ResolveMcaFullyGranted() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(usage: true),
            new ArmProbeResult(
                [new AzureSubscriptionProbe(Guid.NewGuid(), "Production", AgreementType.Mca)],
                CostManagementQueryable: true),
            new BillingProbeResult(
                [new BillingAccountProbe("ba-1", AgreementType.Mca)],
                HasBillingRole: true,
                TransactionsReadable: true));

    // Scenario C — Enterprise Agreement with Azure RBAC but no seat-price API.
    private static CapabilityResolution ResolveEa() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult(
                [new AzureSubscriptionProbe(Guid.NewGuid(), "Enterprise", AgreementType.Ea)],
                CostManagementQueryable: true),
            new BillingProbeResult(
                [new BillingAccountProbe("ea-1", AgreementType.Ea)],
                HasBillingRole: true,
                TransactionsReadable: false));

    // Scenario D — licences resold by a CSP partner.
    private static CapabilityResolution ResolveCspManaged() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(ownerTenantId: PartnerTenant),
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

    [Fact]
    public void Every_scenario_leaves_the_universal_floor_available()
    {
        // The product's central promise: every tenant gets licence data on day one (ADR-002).
        foreach (var resolution in new[] { ResolveMosa(), ResolveMcaFullyGranted(), ResolveEa(), ResolveCspManaged() })
        {
            resolution.Capabilities[Capability.GraphLicensing].Should().BeNull();
        }
    }

    [Fact]
    public void Every_scenario_produces_a_verdict_for_every_capability()
    {
        // A capability with no entry renders as an empty chart, which CLAUDE.md rule 9 calls a bug.
        var expected = Enum.GetValues<Capability>().Where(c => c != Capability.Unknown);

        foreach (var resolution in new[] { ResolveMosa(), ResolveMcaFullyGranted(), ResolveEa(), ResolveCspManaged() })
        {
            resolution.Capabilities.Keys.Should().BeEquivalentTo(expected);
        }
    }

    [Fact]
    public void ScenarioA_mosa_is_classified_from_the_absence_of_any_billing_account()
    {
        var resolution = ResolveMosa();

        resolution.AgreementType.Should().Be(AgreementType.Mosa);
        resolution.IsPartnerManaged.Should().BeFalse();
    }

    [Fact]
    public void ScenarioA_mosa_offers_manual_pricing_rather_than_a_consent_screen()
    {
        // No API exists, so a remediation link to a billing role would be a dead end.
        var prices = ResolveMosa().Capabilities[Capability.BillingTransactions];

        prices.Should().NotBeNull();
        prices!.Reason.Should().Be(CapabilityUnavailableReason.Mosa);
        prices.Guide.Should().Be(RemediationGuide.ManualPricing);
    }

    [Fact]
    public void ScenarioB_mca_with_every_grant_unlocks_everything_except_partner_features()
    {
        var resolution = ResolveMcaFullyGranted();

        resolution.AgreementType.Should().Be(AgreementType.Mca);
        resolution.Capabilities[Capability.GraphUsage].Should().BeNull();
        resolution.Capabilities[Capability.ArmAccess].Should().BeNull();
        resolution.Capabilities[Capability.CostManagement].Should().BeNull();
        resolution.Capabilities[Capability.BillingAccount].Should().BeNull();
        resolution.Capabilities[Capability.BillingTransactions].Should().BeNull();

        resolution.Capabilities[Capability.PartnerCenter]!.Reason
            .Should().Be(CapabilityUnavailableReason.NotPartner);
    }

    [Fact]
    public void ScenarioB_mca_without_a_billing_role_points_at_the_billing_role_guide()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult(
                [new AzureSubscriptionProbe(Guid.NewGuid(), "Production", AgreementType.Mca)],
                CostManagementQueryable: true),
            new BillingProbeResult([new BillingAccountProbe("ba-1", AgreementType.Mca)], HasBillingRole: false, TransactionsReadable: false));

        resolution.AgreementType.Should().Be(AgreementType.Mca);

        var billingAccount = resolution.Capabilities[Capability.BillingAccount];
        billingAccount!.Reason.Should().Be(CapabilityUnavailableReason.BillingRoleMissing);
        billingAccount.Guide.Should().Be(RemediationGuide.BillingRole);

        // Azure cost is unaffected: the two authorisation systems are independent.
        resolution.Capabilities[Capability.CostManagement].Should().BeNull();
    }

    [Fact]
    public void ScenarioC_ea_unlocks_azure_cost_but_not_seat_prices()
    {
        var resolution = ResolveEa();

        resolution.AgreementType.Should().Be(AgreementType.Ea);
        resolution.Capabilities[Capability.CostManagement].Should().BeNull();

        var prices = resolution.Capabilities[Capability.BillingTransactions];
        prices!.Reason.Should().Be(CapabilityUnavailableReason.NotMca);
        prices.Guide.Should().Be(RemediationGuide.ManualPricing, "an EA price sheet is uploaded, not fetched");
    }

    [Fact]
    public void ScenarioC_ea_missing_rbac_is_told_about_the_enrolment_settings()
    {
        // EA customers hit a second obstacle beyond the role: the enrolment's view-charges
        // switches. Omitting that produces a support ticket rather than a fix.
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult(
                [new AzureSubscriptionProbe(Guid.NewGuid(), "Enterprise", AgreementType.Ea)],
                CostManagementQueryable: false),
            new BillingProbeResult([new BillingAccountProbe("ea-1", AgreementType.Ea)], true, false));

        var cost = resolution.Capabilities[Capability.CostManagement];
        cost!.Reason.Should().Be(CapabilityUnavailableReason.RbacMissing);
        cost.Detail.Should().Contain("view charges");
    }

    [Fact]
    public void ScenarioD_csp_managed_is_detected_from_ownerTenantId()
    {
        var resolution = ResolveCspManaged();

        resolution.AgreementType.Should().Be(AgreementType.CspManaged);
        resolution.IsPartnerManaged.Should().BeTrue();
        resolution.ManagingPartnerTenantId.Should().Be(PartnerTenant);
    }

    [Fact]
    public void ScenarioD_csp_managed_is_pointed_at_the_partner_guide_not_a_billing_role()
    {
        var resolution = ResolveCspManaged();

        foreach (var capability in new[] { Capability.BillingAccount, Capability.BillingTransactions })
        {
            var unavailable = resolution.Capabilities[capability];
            unavailable!.Reason.Should().Be(CapabilityUnavailableReason.CspManaged);
            unavailable.Guide.Should().Be(RemediationGuide.PartnerManaged);
        }
    }

    [Fact]
    public void Partner_management_wins_over_a_visible_billing_account()
    {
        // A CSP customer on an Azure Plan can have a billing account visible. Classifying that
        // as a direct agreement would promise prices the partner, not Microsoft, controls.
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(ownerTenantId: PartnerTenant),
            new ArmProbeResult([new AzureSubscriptionProbe(Guid.NewGuid(), "CSP Azure", AgreementType.Mpa)], true),
            new BillingProbeResult([new BillingAccountProbe("mpa-1", AgreementType.Mpa)], true, true));

        resolution.AgreementType.Should().Be(AgreementType.CspManaged);
        resolution.Capabilities[Capability.BillingTransactions]!.Reason
            .Should().Be(CapabilityUnavailableReason.CspManaged);

        // Azure cost still works: an Azure Plan does expose consumption to the customer.
        resolution.Capabilities[Capability.CostManagement].Should().BeNull();
    }

    [Fact]
    public void Classic_csp_azure_is_distinguished_from_a_missing_role()
    {
        // Both look like "no cost data", but only one can be fixed by assigning a role.
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult(
                [new AzureSubscriptionProbe(Guid.NewGuid(), "Classic", AgreementType.Unknown, IsAzurePlan: false)],
                CostManagementQueryable: false),
            BillingProbeResult.NotReachable);

        var cost = resolution.Capabilities[Capability.CostManagement];
        cost!.Reason.Should().Be(CapabilityUnavailableReason.ClassicCsp);
        cost.Guide.Should().NotBe(RemediationGuide.AzureRbac);
    }

    [Fact]
    public void Usage_insights_stay_locked_until_the_separate_consent_is_granted()
    {
        var usage = ResolveMosa().Capabilities[Capability.GraphUsage];

        usage!.Reason.Should().Be(CapabilityUnavailableReason.Tier2NotGranted);
        usage.Guide.Should().Be(RemediationGuide.UsageInsights);
    }

    [Fact]
    public void A_tenant_with_no_graph_access_is_not_misclassified_as_mosa()
    {
        // Nothing readable means "we have not discovered", not "this is a web-direct customer".
        var resolution = CapabilityResolver.Resolve(
            GraphProbeResult.NotReachable,
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

        resolution.AgreementType.Should().Be(AgreementType.NotDiscovered);
        resolution.Capabilities[Capability.GraphLicensing]!.Reason
            .Should().Be(CapabilityUnavailableReason.ConsentRevoked);
    }

    [Fact]
    public void Agreement_type_falls_back_to_the_azure_billing_property_when_no_billing_role_exists()
    {
        // A very common state: RBAC granted, billing role not. The subscription's billing
        // property still identifies the agreement, which is what the checklist wording needs.
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult([new AzureSubscriptionProbe(Guid.NewGuid(), "Production", AgreementType.Mca)], true),
            BillingProbeResult.NotReachable);

        resolution.AgreementType.Should().Be(AgreementType.Mca);
        resolution.Capabilities[Capability.BillingAccount]!.Reason
            .Should().Be(CapabilityUnavailableReason.NoBillingAccount);
    }
}
