using System.Diagnostics;
using FluentAssertions;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Identity;

namespace Mlcp.IntegrationTests.MicrosoftApi;

/// <summary>
/// End-to-end probes over HTTP (ADR-016, ADR-017, ADR-020) against recorded-shape fixtures in
/// <c>tests/Fixtures/MicrosoftApi</c>. Never calls Microsoft; needs no SQL.
/// </summary>
[Trait("Category", "MicrosoftApi")]
public sealed class CapabilityProbeWireMockTests : IDisposable
{
    private const string CostQuery = "*/providers/Microsoft.CostManagement/query";

    private readonly MicrosoftApiHarness _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task A_tier1_only_tenant_is_active_with_usage_not_granted()
    {
        _api.GraphFloor().UsageRefused().NoAzure().NoBilling();

        var outcome = await _api.DiscoverAsync();

        outcome.Status.Should().Be(DiscoveryStatus.Completed);
        _api.Tenant.Status.Should().Be(TenantStatus.Active);

        var profile = _api.Store.Profile!;
        profile.IsAvailable(Capability.GraphLicensing).Should().BeTrue();
        profile.GetUnavailable(Capability.GraphUsage)!.Reason.Should().Be(CapabilityUnavailableReason.Tier2NotGranted);
        profile.VerifiedDomains.Should().Equal("contoso.example", "contoso.onmicrosoft.com");
        _api.Tenant.DisplayName.Should().Be("Contoso");
        _api.Tenant.DefaultDomain.Should().Be("contoso.example");
        _api.Tenant.AgreementTypePrimary.Should().Be(AgreementType.Undetermined);

        // The usage report went out with the Usage Insights token, and the floor with the core one.
        _api.Requests("/v1.0/reports/").Single().Headers!["Authorization"].Should().ContainSingle().Which.Should().Be("Bearer stub-GraphReports");
        _api.Requests("/v1.0/subscribedSkus").Single().Headers!["Authorization"].Should().ContainSingle().Which.Should().Be("Bearer stub-Graph");
    }

    [Fact]
    public async Task A_usage_app_with_no_service_principal_is_tier2_not_granted()
    {
        _api.GraphFloor().NoAzure().NoBilling();
        _api.Tokens.Failures[TokenAudience.GraphReports] = new TokenAcquisitionException(
            MicrosoftApp.UsageInsights, new TokenFailure("AADSTS7000229", TokenFailureCategory.Refused), "no principal");

        var outcome = await _api.DiscoverAsync();

        outcome.Status.Should().Be(DiscoveryStatus.Completed);
        _api.Store.Profile!.GetUnavailable(Capability.GraphUsage)!.Reason.Should().Be(CapabilityUnavailableReason.Tier2NotGranted);
        _api.Requests("/v1.0/reports/").Should().BeEmpty();
    }

    [Fact]
    public async Task Mca_without_a_billing_role_or_rbac_is_undetermined_and_prompts_guide_c()
    {
        _api.GraphFloor().UsageRefused()
            .Stub("GET", "/subscriptions", 403, "arm/error-forbidden-403.json")
            .NoBilling();

        await _api.DiscoverAsync();

        _api.Tenant.AgreementTypePrimary.Should().Be(AgreementType.Undetermined);
        _api.Tenant.Status.Should().Be(TenantStatus.Active);

        var billing = _api.Store.Profile!.GetUnavailable(Capability.BillingTransactions)!;
        billing.Reason.Should().Be(CapabilityUnavailableReason.BillingRoleMissing);
        billing.Guide.Should().Be(RemediationGuide.BillingRole);
        billing.Detail.Should().Be(CapabilityResolver.UndeterminedBillingCopy);

        _api.Store.Profile.GetUnavailable(Capability.ArmAccess)!.Reason.Should().Be(CapabilityUnavailableReason.RbacMissing);
    }

    [Fact]
    public async Task Mca_with_a_billing_role_reads_unbilled_transactions_and_uses_billing_scope()
    {
        _api.GraphFloor().UsageRefused()
            .Stub("GET", "/subscriptions", 200, "arm/subscriptions-mca.json")
            .Stub("GET", "/subscriptions/*/providers/Microsoft.Billing/billingProperty/default", 200, "arm/billingProperty-mca.json")
            .Stub("POST", CostQuery, 200, "costmgmt/query-mtd-mca.json")
            .Stub("GET", "/providers/Microsoft.Billing/billingAccounts", 200, "billing/billingAccounts-mca.json")
            .Stub("GET", "/providers/Microsoft.Billing/billingAccounts/*/billingRoleAssignments", 200, "billing/billingRoleAssignments-mca.json")
            .Stub("GET", "/providers/Microsoft.Billing/billingAccounts/*/billingProfiles", 200, "billing/billingProfiles-mca.json")
            .Stub("GET", "/providers/Microsoft.Billing/billingAccounts/*/billingProfiles/*/transactions", 200, "billing/transactions-unbilled-mca.json");

        await _api.DiscoverAsync();

        _api.Tenant.AgreementTypePrimary.Should().Be(AgreementType.Mca);
        var profile = _api.Store.Profile!;
        profile.IsAvailable(Capability.BillingTransactions).Should().BeTrue();
        profile.IsAvailable(Capability.BillingAccount).Should().BeTrue();
        profile.IsAvailable(Capability.CostManagement).Should().BeTrue();
        profile.DiscoveryDetail.CostScopeRung.Should().Be(CostQueryScopeRung.BillingScope);
        profile.DiscoveryDetail.PurchasesVisible.Should().BeTrue();
        profile.DiscoveryDetail.Subscriptions.Should().ContainSingle().Which.IsAzurePlan.Should().BeTrue();

        // Transactions 2024-04-01: periodStartDate, periodEndDate and type are all required.
        var transactions = _api.Requests("/transactions").Single().Query!;
        transactions["api-version"].Should().Equal("2024-04-01");
        transactions["type"].Should().Equal("Unbilled");
        transactions.Should().ContainKey("periodStartDate").And.ContainKey("periodEndDate");
        transactions.Should().NotContainKey("startDate");

        // ClientType is sent on Cost Management queries, and only there.
        foreach (var query in _api.Requests("/Microsoft.CostManagement/query"))
        {
            query.Headers!["ClientType"].Should().Equal("Mlcp");
        }

        _api.Requests("/billingAccounts").Where(e => !e.Path.Contains("CostManagement", StringComparison.Ordinal))
            .Should().OnlyContain(e => !e.Headers!.ContainsKey("ClientType"));

        // Management groups are never queried for MCA.
        _api.Requests("/managementGroups/").Should().BeEmpty();
    }

    [Fact]
    public async Task An_enterprise_agreement_is_classified_and_uses_the_billing_account_scope()
    {
        _api.GraphFloor().UsageRefused()
            .Stub("GET", "/subscriptions", 200, "arm/subscriptions-ea.json")
            .Stub("GET", "/subscriptions/*/providers/Microsoft.Billing/billingProperty/default", 200, "arm/billingProperty-ea.json")
            .Stub("POST", CostQuery, 200, "costmgmt/query-mtd-mca.json")
            .Stub("GET", "/providers/Microsoft.Billing/billingAccounts", 200, "billing/billingAccounts-ea.json")
            .Stub("GET", "/providers/Microsoft.Billing/billingAccounts/*/billingRoleAssignments", 200, "billing/billingRoleAssignments-mca.json");

        await _api.DiscoverAsync();

        _api.Tenant.AgreementTypePrimary.Should().Be(AgreementType.Ea);
        var profile = _api.Store.Profile!;
        profile.GetUnavailable(Capability.BillingTransactions)!.Reason.Should().Be(CapabilityUnavailableReason.NotMca);
        profile.IsAvailable(Capability.CostManagement).Should().BeTrue();
        profile.DiscoveryDetail.CostScopeRung.Should().Be(CostQueryScopeRung.BillingScope);

        // EA is the one agreement where the root management group is also probed (rung 2).
        _api.Requests($"/managementGroups/{MicrosoftApiHarness.TenantId:D}/").Should().ContainSingle();
        _api.Requests("/transactions").Should().BeEmpty("EA exposes no seat prices through transactions");
    }

    [Fact]
    public async Task A_csp_managed_tenant_is_detected_from_the_partner_owner()
    {
        _api.GraphFloor("graph/directorySubscriptions-csp-managed.json").UsageRefused().NoAzure().NoBilling();

        await _api.DiscoverAsync();

        _api.Tenant.AgreementTypePrimary.Should().Be(AgreementType.CspManaged);
        _api.Tenant.IsPartnerManaged.Should().BeTrue();
        _api.Tenant.ManagingPartnerTenantId.Should().Be(Guid.Parse("00000000-0000-0000-0000-0000000000d2"));
        _api.Store.Profile!.GetUnavailable(Capability.BillingTransactions)!.Guide.Should().Be(RemediationGuide.PartnerManaged);
    }

    [Fact]
    public async Task A_floor_403_moves_the_tenant_to_needs_reconsent_and_stops()
    {
        _api.Stub("GET", "/v1.0/subscribedSkus", 403, "graph/error-forbidden-403.json")
            .Stub("GET", "/v1.0/directory/subscriptions", 200, "graph/directorySubscriptions-mca.json")
            .Stub("GET", "/v1.0/organization", 200, "graph/organization-mca.json")
            .UsageRefused().NoAzure().NoBilling();

        var outcome = await _api.DiscoverAsync();

        outcome.Status.Should().Be(DiscoveryStatus.TenantNeedsReconsent);
        _api.Tenant.Status.Should().Be(TenantStatus.NeedsReconsent);
        _api.Tenant.NeedsReconsentReason.Should().Be("FloorPermissionRemoved:Graph:403:Authorization_RequestDenied");
        _api.Store.Profile!.GetUnavailable(Capability.GraphLicensing)!.Reason.Should().Be(CapabilityUnavailableReason.ConsentRevoked);
        _api.Requests("/v1.0/subscribedSkus").Should().ContainSingle("a 403 is never retried");
        _api.Requests("/subscriptions").Should().OnlyContain(r => r.Path.StartsWith("/v1.0/", StringComparison.Ordinal), "nothing else is called once the floor is refused");
        _api.Requests("/providers/Microsoft.Billing").Should().BeEmpty();
    }

    [Fact]
    public async Task A_graph_503_is_retried_and_the_tenant_activates()
    {
        _api.StubSequence(
                "/v1.0/subscribedSkus",
                (503, "graph/error-unavailable-503.json", []),
                (200, "graph/subscribedSkus-mca.json"))
            .Stub("GET", "/v1.0/directory/subscriptions", 200, "graph/directorySubscriptions-mca.json")
            .Stub("GET", "/v1.0/organization", 200, "graph/organization-mca.json")
            .UsageRefused().NoAzure().NoBilling();

        var outcome = await _api.DiscoverAsync();

        outcome.Status.Should().Be(DiscoveryStatus.Completed);
        _api.Tenant.Status.Should().Be(TenantStatus.Active);
        _api.Requests("/v1.0/subscribedSkus").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_429_retry_after_is_honoured()
    {
        _api.StubSequence(
                "/v1.0/subscribedSkus",
                (429, "graph/error-throttled-429.json", [("Retry-After", "1")]),
                (200, "graph/subscribedSkus-mca.json"))
            .Stub("GET", "/v1.0/directory/subscriptions", 200, "graph/directorySubscriptions-mca.json")
            .Stub("GET", "/v1.0/organization", 200, "graph/organization-mca.json")
            .UsageRefused().NoAzure().NoBilling();

        var stopwatch = Stopwatch.StartNew();
        var outcome = await _api.DiscoverAsync();
        stopwatch.Stop();

        outcome.Status.Should().Be(DiscoveryStatus.Completed);

        var calls = _api.Requests("/v1.0/subscribedSkus").OrderBy(e => e.DateTime).ToList();
        calls.Should().HaveCount(2);
        (calls[1].DateTime - calls[0].DateTime).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(950));
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(950));
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_previous_verdict()
    {
        _api.GraphFloor().UsageRefused()
            .StubSequence(
                "/subscriptions",
                (200, "arm/subscriptions-mca.json", []),
                (503, "arm/error-unavailable-503.json"))
            .Stub("GET", "/subscriptions/*/providers/Microsoft.Billing/billingProperty/default", 200, "arm/billingProperty-mca.json")
            .Stub("POST", CostQuery, 200, "costmgmt/query-mtd-mca.json")
            .NoBilling();

        await _api.DiscoverAsync();
        _api.Store.Profile!.IsAvailable(Capability.CostManagement).Should().BeTrue();
        _api.Tenant.AgreementTypePrimary.Should().Be(AgreementType.Mca);

        var second = await _api.DiscoverAsync();

        second.Status.Should().Be(DiscoveryStatus.Completed, "the floor still answered");
        var cost = _api.Store.Profile.Statuses[Capability.CostManagement];
        cost.IsAvailable.Should().BeTrue("a 503 must not downgrade a capability");
        cost.IsStale.Should().BeTrue();
        cost.StaleDetail.Should().Contain("503");
        _api.Store.Profile.Statuses[Capability.ArmAccess].IsAvailable.Should().BeTrue();
        _api.Tenant.AgreementTypePrimary.Should().Be(AgreementType.Mca);
        _api.Tenant.Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public async Task Consent_verification_is_verified_by_an_organization_read()
    {
        _api.Stub("GET", "/v1.0/organization", 200, "graph/organization-id-only.json");

        var result = await _api.VerifyConsentAsync();

        result.Status.Should().Be(ConsentVerificationStatus.Verified, result.Detail);
        var request = _api.Requests("/v1.0/organization").Single();
        request.Query!["$select"].Should().Equal("id");
        _api.Tenant.Status.Should().Be(TenantStatus.Provisioning, "the verifier never changes the tenant");
    }

    [Fact]
    public async Task Consent_verification_reports_a_403_as_not_granted_without_flagging()
    {
        _api.Stub("GET", "/v1.0/organization", 403, "graph/error-forbidden-403.json");

        var result = await _api.VerifyConsentAsync();

        result.Status.Should().Be(ConsentVerificationStatus.NotGranted);
        _api.Tenant.Status.Should().Be(TenantStatus.Provisioning);
    }

    [Fact]
    public async Task Consent_verification_during_propagation_is_pending()
    {
        using var fresh = new MicrosoftApiHarness(FreshlyConsentingTenant());
        fresh.Tokens.Failures[TokenAudience.Graph] = new TokenAcquisitionException(
            MicrosoftApp.Core, new TokenFailure("AADSTS700016", TokenFailureCategory.Refused), "not yet");

        var result = await fresh.VerifyConsentAsync();

        result.Status.Should().Be(ConsentVerificationStatus.PendingPropagation);
        fresh.Tenant.Status.Should().Be(TenantStatus.ConsentPendingVerification);
    }

    [Fact]
    public async Task Consent_verification_after_the_window_is_not_granted()
    {
        _api.Tokens.Failures[TokenAudience.Graph] = new TokenAcquisitionException(
            MicrosoftApp.Core, new TokenFailure("AADSTS700016", TokenFailureCategory.Refused), "gone");

        var result = await _api.VerifyConsentAsync();

        result.Status.Should().Be(ConsentVerificationStatus.NotGranted);
    }

    [Fact]
    public async Task Consent_verification_on_a_5xx_is_failed_not_a_verdict()
    {
        _api.Stub("GET", "/v1.0/organization", 503, "graph/error-unavailable-503.json");

        var result = await _api.VerifyConsentAsync();

        result.Status.Should().Be(ConsentVerificationStatus.Failed);
    }

    private static Tenant FreshlyConsentingTenant()
    {
        var tenant = Tenant.Register(MicrosoftApiHarness.TenantId, "Contoso", null, "westeurope", DateTimeOffset.UtcNow);
        tenant.AwaitConsentVerification(Guid.NewGuid(), DateTimeOffset.UtcNow);
        return tenant;
    }
}
