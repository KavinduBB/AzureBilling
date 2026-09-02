using FluentAssertions;
using Mlcp.Domain.Sync;
using Mlcp.Sync.Scheduling;

namespace Mlcp.UnitTests.Sync;

/// <summary>
/// The stagger is what stops every tenant's Microsoft calls landing in the same minute. Graph
/// throttles per application per tenant and Cost Management per ClientType, so a synchronised
/// fleet is the fastest route to throttling every customer at once
/// (docs/02-api-reference.md §1.3, §2.3).
/// </summary>
public class SyncScheduleTests
{
    [Fact]
    public void The_offset_for_a_tenant_is_stable_across_calls()
    {
        // Deliberately not GetHashCode: string and Guid hashing is randomised per process, so an
        // offset built on it would change on every restart and the stagger would not hold.
        var tenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var first = SyncSchedule.StaggerOffsetMinutes(tenantId);
        var second = SyncSchedule.StaggerOffsetMinutes(tenantId);

        second.Should().Be(first);
    }

    [Fact]
    public void The_offset_falls_inside_the_stagger_window()
    {
        foreach (var _ in Enumerable.Range(0, 200))
        {
            var offset = SyncSchedule.StaggerOffsetMinutes(Guid.NewGuid());

            offset.Should().BeGreaterThanOrEqualTo(0);
            offset.Should().BeLessThan(SyncSchedule.StaggerWindowMinutes);
        }
    }

    [Fact]
    public void Offsets_spread_across_the_window_rather_than_clustering()
    {
        // A hash that mapped most tenants onto a few minutes would satisfy the range check while
        // still producing the thundering herd the stagger exists to prevent.
        var offsets = Enumerable.Range(0, 1000)
            .Select(_ => SyncSchedule.StaggerOffsetMinutes(Guid.NewGuid()))
            .ToList();

        offsets.Distinct().Should().HaveCountGreaterThan(45, "1000 tenants should reach most of a 60-minute window");

        var largestBucket = offsets.GroupBy(o => o).Max(g => g.Count());
        largestBucket.Should().BeLessThan(60, "no single minute should hold a disproportionate share");
    }

    [Fact]
    public void Different_tenants_generally_get_different_offsets()
    {
        var a = SyncSchedule.StaggerOffsetMinutes(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
        var b = SyncSchedule.StaggerOffsetMinutes(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

        a.Should().NotBe(b);
    }

    [Theory]
    [InlineData(SyncJobType.LicenseSkuSync, 4)]
    [InlineData(SyncJobType.BillingSubscriptionSync, 6)]
    [InlineData(SyncJobType.AzureCostSummarySync, 6)]
    public void Job_intervals_match_the_documented_cadence(SyncJobType jobType, int expectedHours)
    {
        SyncSchedule.Interval(jobType).Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Fact]
    public void Capability_discovery_reruns_weekly()
    {
        SyncSchedule.Interval(SyncJobType.CapabilityDiscovery).Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void A_message_id_identifies_one_job_for_one_tenant_in_one_slot()
    {
        // This is what makes Service Bus duplicate detection effective: a scheduler that
        // restarts mid-pass re-enqueues the same id and the broker discards it.
        var tenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var slot = new DateTimeOffset(2026, 9, 2, 10, 15, 0, TimeSpan.Zero);

        var first = new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, slot, "corr-1");
        var second = new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, slot, "corr-2");

        second.MessageId.Should().Be(first.MessageId, "a different correlation id is still the same unit of work");
    }

    [Fact]
    public void A_later_slot_is_a_different_unit_of_work()
    {
        var tenantId = Guid.NewGuid();
        var slot = new DateTimeOffset(2026, 9, 2, 10, 15, 0, TimeSpan.Zero);

        var first = new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, slot, "c");
        var later = new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, slot.AddHours(4), "c");

        later.MessageId.Should().NotBe(first.MessageId);
    }

    [Fact]
    public void The_session_id_is_the_tenant_so_ordering_holds_within_a_tenant()
    {
        var tenantId = Guid.NewGuid();

        new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, DateTimeOffset.UnixEpoch, "c")
            .SessionId.Should().Be(tenantId.ToString("D"));
    }
}
