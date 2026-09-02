using FluentAssertions;
using Mlcp.Application.Common;
using Mlcp.Domain.Capabilities;

namespace Mlcp.UnitTests.Capabilities;

public class CapabilityProfileTests
{
    private static readonly Guid Tenant = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_profile_reports_every_capability_as_not_yet_discovered()
    {
        // "Not discovered" and "discovered to be absent" must not look the same. A tenant
        // mid-provisioning would otherwise be shown the same message as one whose agreement
        // genuinely exposes no prices.
        var profile = TenantCapabilityProfile.Undiscovered(Tenant, Now);

        foreach (var capability in Enum.GetValues<Capability>().Where(c => c != Capability.Unknown))
        {
            profile.IsAvailable(capability).Should().BeFalse();
            profile.GetUnavailable(capability)!.Reason.Should().Be(CapabilityUnavailableReason.NotDiscovered);
        }
    }

    [Fact]
    public void An_available_capability_returns_no_unavailable_reason()
    {
        var profile = TenantCapabilityProfile.Undiscovered(Tenant, Now);
        profile.MarkAvailable(Capability.GraphLicensing, Now);

        profile.IsAvailable(Capability.GraphLicensing).Should().BeTrue();
        profile.GetUnavailable(Capability.GraphLicensing).Should().BeNull();
    }

    [Fact]
    public void An_unavailable_capability_keeps_its_reason_and_guide()
    {
        var profile = TenantCapabilityProfile.Undiscovered(Tenant, Now);

        profile.MarkUnavailable(
            new CapabilityUnavailable(
                Capability.CostManagement,
                CapabilityUnavailableReason.RbacMissing,
                RemediationGuide.AzureRbac,
                "No Cost Management Reader assignment found at any visible scope."),
            Now);

        var unavailable = profile.GetUnavailable(Capability.CostManagement);

        unavailable.Should().NotBeNull();
        unavailable!.Reason.Should().Be(CapabilityUnavailableReason.RbacMissing);
        unavailable.Guide.Should().Be(RemediationGuide.AzureRbac);
        unavailable.IsRemediable.Should().BeTrue();
        unavailable.Detail.Should().Contain("Cost Management Reader");
    }

    [Fact]
    public void A_capability_the_agreement_cannot_ever_expose_is_not_remediable()
    {
        // A MOSA tenant cannot unlock seat prices by granting anything: the API does not exist.
        // Offering a remediation link would send the customer on an errand that cannot succeed.
        var unavailable = new CapabilityUnavailable(
            Capability.BillingTransactions,
            CapabilityUnavailableReason.Mosa,
            RemediationGuide.ManualPricing);

        unavailable.IsRemediable.Should().BeTrue("manual pricing is a genuine alternative");

        var noWayForward = new CapabilityUnavailable(
            Capability.PartnerCenter,
            CapabilityUnavailableReason.IndirectReseller,
            RemediationGuide.None);

        noWayForward.IsRemediable.Should().BeFalse();
    }

    [Fact]
    public void Re_probing_a_capability_replaces_the_previous_verdict()
    {
        var profile = TenantCapabilityProfile.Undiscovered(Tenant, Now);

        profile.MarkUnavailable(
            new CapabilityUnavailable(Capability.ArmAccess, CapabilityUnavailableReason.NoAzure, RemediationGuide.AzureRbac),
            Now);

        profile.MarkAvailable(Capability.ArmAccess, Now.AddHours(1));

        profile.IsAvailable(Capability.ArmAccess).Should().BeTrue();
        profile.Statuses[Capability.ArmAccess].CheckedUtc.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void Completing_a_profiling_pass_schedules_the_next_one()
    {
        var profile = TenantCapabilityProfile.Undiscovered(Tenant, Now);

        profile.CompleteProfiling(Now, TimeSpan.FromDays(7));

        profile.LastProfiledUtc.Should().Be(Now);
        profile.NextProfileUtc.Should().Be(Now.AddDays(7));
    }

    [Fact]
    public void Granting_an_unlock_brings_the_next_discovery_forward()
    {
        var profile = TenantCapabilityProfile.Undiscovered(Tenant, Now);
        profile.CompleteProfiling(Now, TimeSpan.FromDays(7));

        profile.InvalidateNow(Now.AddMinutes(5));

        profile.NextProfileUtc.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public void An_unavailable_result_carries_the_reason_instead_of_an_empty_value()
    {
        // The point of Result<T>: a caller cannot reach a value without deciding what to render
        // when there is not one.
        Result<IReadOnlyList<string>> result = new CapabilityUnavailable(
            Capability.BillingTransactions,
            CapabilityUnavailableReason.BillingRoleMissing,
            RemediationGuide.BillingRole);

        result.IsAvailable.Should().BeFalse();

        var rendered = result.Match(
            onAvailable: values => $"{values.Count} rows",
            onUnavailable: reason => $"unavailable: {reason.Reason}");

        rendered.Should().Be("unavailable: BillingRoleMissing");

        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Mapping_an_unavailable_result_carries_the_reason_through()
    {
        Result<int> result = new CapabilityUnavailable(
            Capability.GraphUsage,
            CapabilityUnavailableReason.Tier2NotGranted,
            RemediationGuide.UsageInsights);

        var mapped = result.Map(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        mapped.IsAvailable.Should().BeFalse();
        mapped.Unavailable!.Reason.Should().Be(CapabilityUnavailableReason.Tier2NotGranted);
    }
}
