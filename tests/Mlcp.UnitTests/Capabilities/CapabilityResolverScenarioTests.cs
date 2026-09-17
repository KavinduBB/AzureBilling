using FluentAssertions;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Resilience;

namespace Mlcp.UnitTests.Capabilities;

/// <summary>
/// P0-7's acceptance criterion, as amended by ADR-020: for MOSA, MCA, EA and CSP-managed fixtures
/// the resolved profile matches the scenario table, and a tenant whose agreement cannot be seen is
/// <see cref="AgreementType.Undetermined"/> — never MOSA by default.
/// </summary>
/// <remarks>
/// Each test asserts the reason and the guide, not just the boolean: "unavailable" with the wrong
/// "why" sends a customer to a remediation guide that cannot help them.
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

    private static AzureSubscriptionProbe Subscription(AgreementType agreement, string name = "Production", bool? azurePlan = true)
        => new(Guid.NewGuid(), name, agreement, azurePlan);

    // Scenario A — web-direct MOSA, with positive evidence: the subscription billing property.
    private static CapabilityResolution ResolveMosa() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult([Subscription(AgreementType.Mosa, "Pay-As-You-Go")], CostManagementQueryable: true),
            BillingProbeResult.NotReachable);

    // Graph only: nothing reveals the agreement.
    private static CapabilityResolution ResolveGraphOnly() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

    // Scenario B — MCA direct with a billing role granted.
    private static CapabilityResolution ResolveMcaFullyGranted() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(usage: true),
            new ArmProbeResult([Subscription(AgreementType.Mca)], CostManagementQueryable: true),
            new BillingProbeResult(
                [new BillingAccountProbe("ba-1", AgreementType.Mca)],
                HasBillingRole: true,
                TransactionsReadable: true));

    // Scenario C — Enterprise Agreement with Azure RBAC but no seat-price API.
    private static CapabilityResolution ResolveEa() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult([Subscription(AgreementType.Ea, "Enterprise")], CostManagementQueryable: true),
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

    // ADR-020 — MCA customer who has granted neither RBAC nor a billing role.
    private static CapabilityResolution ResolveMcaWithoutBillingRole() =>
        CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            ArmProbeResult.NotReachable,
            new BillingProbeResult([], HasBillingRole: false, TransactionsReadable: false, AccountList: ProbeCallOutcome.Success));

    private static IEnumerable<CapabilityResolution> AllScenarios()
        => [ResolveMosa(), ResolveMcaFullyGranted(), ResolveEa(), ResolveCspManaged(), ResolveMcaWithoutBillingRole()];

    [Fact]
    public void Every_scenario_leaves_the_universal_floor_available()
    {
        foreach (var resolution in AllScenarios())
        {
            resolution.Capabilities[Capability.GraphLicensing].Should().BeNull();
        }
    }

    [Fact]
    public void Every_scenario_produces_a_verdict_with_a_detail_for_every_capability()
    {
        var expected = Enum.GetValues<Capability>().Where(c => c != Capability.Unknown);

        foreach (var resolution in AllScenarios())
        {
            resolution.Capabilities.Keys.Should().BeEquivalentTo(expected);

            foreach (var unavailable in resolution.Capabilities.Values.Where(v => v is not null))
            {
                unavailable!.Detail.Should().NotBeNullOrWhiteSpace($"{unavailable.Capability} must explain itself");
            }
        }
    }

    [Fact]
    public void ScenarioA_mosa_is_classified_from_the_online_services_billing_property()
    {
        var resolution = ResolveMosa();

        resolution.AgreementType.Should().Be(AgreementType.Mosa);
        resolution.IsPartnerManaged.Should().BeFalse();
    }

    [Fact]
    public void ScenarioA_mosa_is_classified_when_billing_is_visible_and_holds_no_mca_or_ea_account()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            ArmProbeResult.NotReachable,
            new BillingProbeResult([], false, false, AccountList: ProbeCallOutcome.Success, RoleAssignments: ProbeCallOutcome.Success));

        resolution.AgreementType.Should().Be(AgreementType.Mosa);
    }

    [Fact]
    public void ScenarioA_mosa_offers_manual_pricing_rather_than_a_consent_screen()
    {
        var prices = ResolveMosa().Capabilities[Capability.BillingTransactions];

        prices!.Reason.Should().Be(CapabilityUnavailableReason.Mosa);
        prices.Guide.Should().Be(RemediationGuide.ManualPricing);
    }

    [Fact]
    public void Graph_alone_is_undetermined_not_mosa()
    {
        // ADR-020: "no MCA account visible" is what every new customer looks like before they grant
        // a billing role. It is not evidence of MOSA.
        var resolution = ResolveGraphOnly();

        resolution.AgreementType.Should().Be(AgreementType.Undetermined);
        resolution.Capabilities[Capability.BillingTransactions]!.Guide.Should().Be(RemediationGuide.BillingRole);
    }

    [Fact]
    public void Mca_without_a_billing_role_is_undetermined_with_an_actionable_billing_role_prompt()
    {
        var resolution = ResolveMcaWithoutBillingRole();

        resolution.AgreementType.Should().Be(AgreementType.Undetermined);

        foreach (var capability in new[] { Capability.BillingAccount, Capability.BillingTransactions })
        {
            var unavailable = resolution.Capabilities[capability]!;
            unavailable.Reason.Should().Be(CapabilityUnavailableReason.BillingRoleMissing);
            unavailable.Guide.Should().Be(RemediationGuide.BillingRole);
            unavailable.IsRemediable.Should().BeTrue();
            unavailable.Detail.Should().Be(CapabilityResolver.UndeterminedBillingCopy);
        }

        // Guide B stays actionable too.
        resolution.Capabilities[Capability.CostManagement]!.Guide.Should().Be(RemediationGuide.AzureRbac);
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
    public void ScenarioB_mca_with_a_billing_role_and_billing_scope_cost_access_uses_rung_1()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            ArmProbeResult.NotReachable,
            new BillingProbeResult(
                [new BillingAccountProbe("ba-1", AgreementType.Mca)],
                HasBillingRole: true,
                TransactionsReadable: true,
                BillingProfileIds: ["/providers/Microsoft.Billing/billingAccounts/ba-1/billingProfiles/bp-1"],
                BillingScopeCostQuery: ProbeCallOutcome.Success));

        resolution.CostScope!.Rung.Should().Be(CostQueryScopeRung.BillingScope);
        resolution.Capabilities[Capability.CostManagement].Should().BeNull("billing scope answers cost queries without RBAC");
    }

    [Fact]
    public void ScenarioB_mca_without_a_billing_role_points_at_the_billing_role_guide()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult([Subscription(AgreementType.Mca)], CostManagementQueryable: true),
            new BillingProbeResult([new BillingAccountProbe("ba-1", AgreementType.Mca)], HasBillingRole: false, TransactionsReadable: false));

        resolution.AgreementType.Should().Be(AgreementType.Mca);

        var billingAccount = resolution.Capabilities[Capability.BillingAccount];
        billingAccount!.Reason.Should().Be(CapabilityUnavailableReason.BillingRoleMissing);
        billingAccount.Guide.Should().Be(RemediationGuide.BillingRole);

        // Azure cost is unaffected, on rung 3 because MCA does not support management groups.
        resolution.Capabilities[Capability.CostManagement].Should().BeNull();
        resolution.CostScope!.Rung.Should().Be(CostQueryScopeRung.PerSubscription);
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
    public void ScenarioC_ea_with_root_management_group_access_uses_rung_2()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult(
                [Subscription(AgreementType.Ea, "Enterprise")],
                CostManagementQueryable: false,
                RootManagementGroupCostQuery: ProbeCallOutcome.Success),
            BillingProbeResult.NotReachable,
            tenantId: Guid.Parse("7e000000-0000-0000-0000-000000000007"));

        resolution.CostScope!.Rung.Should().Be(CostQueryScopeRung.RootManagementGroup);
        resolution.CostScope.Scopes.Should().Equal("/providers/Microsoft.Management/managementGroups/7e000000-0000-0000-0000-000000000007");
        resolution.Capabilities[Capability.CostManagement].Should().BeNull();
    }

    [Fact]
    public void ScenarioC_ea_missing_rbac_is_told_about_the_enrolment_settings()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult([Subscription(AgreementType.Ea, "Enterprise")], CostManagementQueryable: false),
            new BillingProbeResult([new BillingAccountProbe("ea-1", AgreementType.Ea)], true, false));

        var cost = resolution.Capabilities[Capability.CostManagement];
        cost!.Reason.Should().Be(CapabilityUnavailableReason.RbacMissing);
        cost.Detail.Should().Contain("view charges");
        resolution.DeniedCapabilities.Should().Contain(Capability.CostManagement);
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
    public void ScenarioD_csp_managed_is_detected_from_a_partner_agreement_billing_property()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult([Subscription(AgreementType.Mpa, "CSP Azure")], CostManagementQueryable: true),
            BillingProbeResult.NotReachable);

        resolution.AgreementType.Should().Be(AgreementType.CspManaged);
        resolution.IsPartnerManaged.Should().BeTrue();
        resolution.ManagingPartnerTenantId.Should().BeNull("the billing property does not name the partner");
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
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(ownerTenantId: PartnerTenant),
            new ArmProbeResult([Subscription(AgreementType.Mpa, "CSP Azure")], true),
            new BillingProbeResult([new BillingAccountProbe("mpa-1", AgreementType.Mpa)], true, true));

        resolution.AgreementType.Should().Be(AgreementType.CspManaged);
        resolution.Capabilities[Capability.BillingTransactions]!.Reason
            .Should().Be(CapabilityUnavailableReason.CspManaged);

        // Azure cost still works: an Azure Plan exposes consumption to the customer.
        resolution.Capabilities[Capability.CostManagement].Should().BeNull();
    }

    [Fact]
    public void Classic_csp_azure_is_distinguished_from_a_missing_role()
    {
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
    public void An_unknown_azure_plan_status_is_not_treated_as_classic_csp()
    {
        // CLAUDE.md rule 11: unknown is null, and null must never read as either answer.
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult(
                [new AzureSubscriptionProbe(Guid.NewGuid(), "Unclassified", AgreementType.NotDiscovered, IsAzurePlan: null)],
                CostManagementQueryable: false),
            BillingProbeResult.NotReachable);

        resolution.Capabilities[Capability.CostManagement]!.Reason.Should().Be(CapabilityUnavailableReason.RbacMissing);
    }

    [Fact]
    public void Usage_insights_stay_locked_until_the_separate_consent_is_granted()
    {
        var resolution = ResolveMosa();
        var usage = resolution.Capabilities[Capability.GraphUsage];

        usage!.Reason.Should().Be(CapabilityUnavailableReason.Tier2NotGranted);
        usage.Guide.Should().Be(RemediationGuide.UsageInsights);
        resolution.DeniedCapabilities.Should().Contain(Capability.GraphUsage);
    }

    [Fact]
    public void A_tenant_with_no_graph_access_is_not_misclassified_as_mosa()
    {
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
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph(),
            new ArmProbeResult([Subscription(AgreementType.Mca)], true),
            BillingProbeResult.NotReachable);

        resolution.AgreementType.Should().Be(AgreementType.Mca);

        // Without a billing role the account list is simply empty; the fix is a billing role.
        var account = resolution.Capabilities[Capability.BillingAccount]!;
        account.Reason.Should().Be(CapabilityUnavailableReason.BillingRoleMissing);
        account.Guide.Should().Be(RemediationGuide.BillingRole);
    }

    [Fact]
    public void Transient_failures_are_inconclusive_rather_than_negative()
    {
        var transient = new ProbeCallOutcome(MicrosoftFailureKind.Transient, 503);

        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph() with { UsageReports = transient },
            ArmProbeResult.Inconclusive(transient),
            BillingProbeResult.Inconclusive(transient));

        resolution.InconclusiveCapabilities.Should().BeEquivalentTo(
            [Capability.GraphUsage, Capability.ArmAccess, Capability.CostManagement, Capability.BillingAccount, Capability.BillingTransactions]);

        resolution.Capabilities[Capability.ArmAccess]!.Reason.Should().Be(CapabilityUnavailableReason.ProviderError);
        resolution.DeniedCapabilities.Should().BeEmpty();
        resolution.AgreementType.Should().Be(AgreementType.Undetermined);
    }

    [Fact]
    public void A_floor_refusal_revokes_graph_licensing()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph() with { SubscribedSkus = new ProbeCallOutcome(MicrosoftFailureKind.FloorPermissionRemoved, 403) },
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

        resolution.Capabilities[Capability.GraphLicensing]!.Reason.Should().Be(CapabilityUnavailableReason.ConsentRevoked);
    }

    [Fact]
    public void A_transient_floor_failure_does_not_revoke_graph_licensing()
    {
        var resolution = CapabilityResolver.Resolve(
            FullyConsentedGraph() with { DirectorySubscriptions = new ProbeCallOutcome(MicrosoftFailureKind.Transient, 503) },
            ArmProbeResult.NotReachable,
            BillingProbeResult.NotReachable);

        resolution.Capabilities[Capability.GraphLicensing]!.Reason.Should().Be(CapabilityUnavailableReason.ProviderError);
        resolution.InconclusiveCapabilities.Should().Contain(Capability.GraphLicensing);
        resolution.AgreementType.Should().Be(AgreementType.NotDiscovered);
    }
}
