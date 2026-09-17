using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Resilience;

namespace Mlcp.UnitTests.Capabilities;

/// <summary>ADR-016 behaviour of capability discovery: who changes the tenant, and when verdicts are kept.</summary>
public class CapabilityDiscoveryServiceTests
{
    private readonly FakeTimeProvider _clock = new(ProbeFixtures.Now);
    private readonly FakeGraphProbe _graph = new();
    private readonly FakeArmProbe _arm = new();
    private readonly FakeBillingProbe _billing = new();

    private CapabilityDiscoveryService Service(InMemoryOnboardingStore store)
        => new(store, _graph, _arm, _billing, _clock, NullLogger<CapabilityDiscoveryService>.Instance);

    [Theory]
    [InlineData(TenantStatus.NotConnected)]
    [InlineData(TenantStatus.ConsentPendingVerification)]
    [InlineData(TenantStatus.NeedsReconsent)]
    [InlineData(TenantStatus.GracePeriod)]
    public async Task Ineligible_tenants_are_skipped_without_calling_Microsoft(TenantStatus status)
    {
        var tenant = TenantIn(status);
        var store = new InMemoryOnboardingStore(tenant);

        var outcome = await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        outcome.Status.Should().Be(DiscoveryStatus.Skipped);
        (_graph.Calls + _arm.Calls + _billing.Calls).Should().Be(0);
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task A_tier1_only_tenant_becomes_active_with_usage_not_granted()
    {
        var tenant = ProbeFixtures.ProvisioningTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => ProbeFixtures.FloorOnly(usage: false);

        var outcome = await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        outcome.Status.Should().Be(DiscoveryStatus.Completed);
        tenant.Status.Should().Be(TenantStatus.Active);
        store.Profile!.GetUnavailable(Capability.GraphUsage)!.Reason.Should().Be(CapabilityUnavailableReason.Tier2NotGranted);
        store.Profile.IsAvailable(Capability.GraphLicensing).Should().BeTrue();
        tenant.AgreementTypePrimary.Should().Be(AgreementType.Undetermined);
    }

    [Fact]
    public async Task Verified_domains_are_recorded_lower_case()
    {
        var tenant = ProbeFixtures.ProvisioningTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => ProbeFixtures.FloorOnly();

        await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        store.Profile!.VerifiedDomains.Should().Equal("contoso.example", "contoso.onmicrosoft.com");
    }

    [Theory]
    [InlineData(MicrosoftFailureKind.FloorPermissionRemoved, 403)]
    [InlineData(MicrosoftFailureKind.GrantRevoked, 401)]
    public async Task A_floor_refusal_flags_the_tenant_on_the_same_store(MicrosoftFailureKind kind, int status)
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => ProbeFixtures.FloorOnly() with { SubscribedSkus = new ProbeCallOutcome(kind, status) };

        var outcome = await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        outcome.Status.Should().Be(DiscoveryStatus.TenantNeedsReconsent);
        tenant.Status.Should().Be(TenantStatus.NeedsReconsent);
        tenant.NeedsReconsentReason.Should().StartWith(kind.ToString());
        tenant.NextReconsentProbeUtc.Should().Be(ProbeFixtures.Now.AddHours(1));
        store.Profile!.GetUnavailable(Capability.GraphLicensing)!.Reason.Should().Be(CapabilityUnavailableReason.ConsentRevoked);
        store.SaveCount.Should().Be(1);

        // Nothing else is worth calling with the core grant gone.
        _arm.Calls.Should().Be(0);
        _billing.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_refused_non_floor_capability_never_touches_the_tenant()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => ProbeFixtures.FloorOnly();
        _arm.Result = () => ArmProbeResult.Inconclusive(ProbeCallOutcome.Denied) with { SubscriptionList = ProbeCallOutcome.Denied };
        _billing.Result = () => new BillingProbeResult([], false, false, AccountList: ProbeCallOutcome.Denied);

        var outcome = await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        outcome.Status.Should().Be(DiscoveryStatus.Completed);
        tenant.Status.Should().Be(TenantStatus.Active);
        store.Profile!.GetUnavailable(Capability.ArmAccess)!.Reason.Should().Be(CapabilityUnavailableReason.RbacMissing);
    }

    [Fact]
    public async Task A_capability_that_was_available_and_is_now_refused_is_role_revoked()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => ProbeFixtures.FloorOnly();
        _arm.Result = () => ProbeFixtures.McaAzure(costReadable: true);

        await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);
        store.Profile!.IsAvailable(Capability.CostManagement).Should().BeTrue();

        _arm.Result = () => ProbeFixtures.McaAzure(costReadable: false);
        await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        var cost = store.Profile.GetUnavailable(Capability.CostManagement)!;
        cost.Reason.Should().Be(CapabilityUnavailableReason.RoleRevoked);
        cost.Guide.Should().Be(RemediationGuide.AzureRbac);
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_previous_verdict_and_marks_it_stale()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => ProbeFixtures.FloorOnly(usage: true);
        _arm.Result = () => ProbeFixtures.McaAzure(costReadable: true);

        await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(7));
        _graph.Result = () => ProbeFixtures.FloorOnly() with { UsageReports = ProbeFixtures.Transient503 };
        _arm.Result = () => ArmProbeResult.Inconclusive(ProbeFixtures.Transient503);

        var outcome = await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        outcome.Status.Should().Be(DiscoveryStatus.Completed);

        foreach (var capability in new[] { Capability.GraphUsage, Capability.ArmAccess, Capability.CostManagement })
        {
            var status = store.Profile!.Statuses[capability];
            status.IsAvailable.Should().BeTrue($"{capability} must not be downgraded by a 503");
            status.IsStale.Should().BeTrue();
            status.StaleDetail.Should().Contain("503");
        }

        tenant.AgreementTypePrimary.Should().Be(AgreementType.Mca, "losing visibility does not change the agreement");
    }

    [Fact]
    public async Task A_transient_floor_failure_keeps_the_tenant_and_asks_for_a_retry()
    {
        var tenant = ProbeFixtures.ProvisioningTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => GraphProbeResult.Inconclusive(ProbeFixtures.Transient503);

        var outcome = await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        outcome.Status.Should().Be(DiscoveryStatus.Inconclusive);
        outcome.ShouldRetry.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Provisioning);
        store.Profile!.NextProfileUtc.Should().Be(ProbeFixtures.Now + CapabilityDiscoveryService.InconclusiveRetryInterval);
    }

    [Fact]
    public async Task A_probe_that_throws_is_inconclusive_not_negative()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => ProbeFixtures.FloorOnly();
        _arm.Result = () => throw new HttpRequestException("boom");

        var outcome = await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        outcome.Status.Should().Be(DiscoveryStatus.Completed);
        store.Profile!.GetUnavailable(Capability.ArmAccess)!.Reason.Should().Be(CapabilityUnavailableReason.ProviderError);
    }

    [Fact]
    public async Task The_cost_scope_rung_and_subscription_agreements_are_recorded()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Result = () => ProbeFixtures.FloorOnly();
        _arm.Result = () => ProbeFixtures.McaAzure(costReadable: true);

        await Service(store).DiscoverAsync(tenant.TenantId, CancellationToken.None);

        var detail = store.Profile!.DiscoveryDetail;
        detail.CostScopeRung.Should().Be(CostQueryScopeRung.PerSubscription);
        detail.PurchasesVisible.Should().BeFalse();
        detail.Subscriptions.Should().ContainSingle().Which.AgreementType.Should().Be(AgreementType.Mca);
    }

    [Fact]
    public async Task A_floor_reprobe_restores_consent()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        tenant.MarkNeedsReconsent("FloorPermissionRemoved:Graph:403", ProbeFixtures.Now);
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Floor = () => ProbeCallOutcome.Success;

        var status = await Service(store).ReprobeFloorAsync(tenant.TenantId, "corr-r", CancellationToken.None);

        status.Should().Be(FloorReprobeStatus.Restored);
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.NextReconsentProbeUtc.Should().BeNull();
        var audit = store.Audits.Should().ContainSingle().Subject;
        audit.NewValue.Should().Be("ConsentRestored");
        audit.CorrelationId.Should().Be("corr-r");
    }

    [Fact]
    public async Task A_failed_floor_reprobe_schedules_the_next_one()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        tenant.MarkNeedsReconsent("GrantRevoked:Graph:401", ProbeFixtures.Now);
        var store = new InMemoryOnboardingStore(tenant);
        _graph.Floor = () => ProbeCallOutcome.FloorRemoved;
        _clock.Advance(TimeSpan.FromHours(1));

        var status = await Service(store).ReprobeFloorAsync(tenant.TenantId, null, CancellationToken.None);

        status.Should().Be(FloorReprobeStatus.StillFailing);
        store.Audits.Should().BeEmpty();
        tenant.Status.Should().Be(TenantStatus.NeedsReconsent);
        tenant.ReconsentProbeAttempts.Should().Be(1);
        tenant.NextReconsentProbeUtc.Should().Be(ProbeFixtures.Now.AddHours(1 + 6));
    }

    [Fact]
    public async Task A_reprobe_of_an_active_tenant_does_nothing()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new InMemoryOnboardingStore(tenant);

        var status = await Service(store).ReprobeFloorAsync(tenant.TenantId, null, CancellationToken.None);

        status.Should().Be(FloorReprobeStatus.Skipped);
        _graph.Calls.Should().Be(0);
    }

    private static Tenant TenantIn(TenantStatus status)
    {
        var tenant = Tenant.Register(Guid.NewGuid(), "Contoso", null, "westeurope", ProbeFixtures.Now);

        switch (status)
        {
            case TenantStatus.ConsentPendingVerification:
                tenant.AwaitConsentVerification(Guid.NewGuid(), ProbeFixtures.Now);
                break;
            case TenantStatus.NeedsReconsent:
                tenant = ProbeFixtures.ActiveTenant();
                tenant.MarkNeedsReconsent("GrantRevoked", ProbeFixtures.Now);
                break;
            case TenantStatus.GracePeriod:
                tenant = ProbeFixtures.ActiveTenant();
                tenant.BeginGracePeriod(ProbeFixtures.Now, TimeSpan.FromDays(30));
                break;
        }

        tenant.Status.Should().Be(status);
        return tenant;
    }
}
