using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.Integration.Azure.Messaging;
using Mlcp.Shared.Resilience;
using Mlcp.Sync.Scheduling;
using Mlcp.UnitTests.Capabilities;

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
        var tenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        SyncSchedule.StaggerOffsetMinutes(tenantId).Should().Be(SyncSchedule.StaggerOffsetMinutes(tenantId));
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
        var offsets = Enumerable.Range(0, 1000)
            .Select(_ => SyncSchedule.StaggerOffsetMinutes(Guid.NewGuid()))
            .ToList();

        offsets.Distinct().Should().HaveCountGreaterThan(45, "1000 tenants should reach most of a 60-minute window");
        offsets.GroupBy(o => o).Max(g => g.Count()).Should().BeLessThan(60);
    }

    [Fact]
    public void Different_tenants_generally_get_different_offsets()
    {
        var a = SyncSchedule.StaggerOffsetMinutes(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
        var b = SyncSchedule.StaggerOffsetMinutes(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

        a.Should().NotBe(b);
    }

    [Fact]
    public void The_next_slot_is_the_tenants_minute_in_this_hour_or_the_next()
    {
        var tenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var offset = SyncSchedule.StaggerOffsetMinutes(tenantId);
        var hour = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

        SyncSchedule.NextStaggeredSlot(tenantId, hour).Should().Be(hour.AddMinutes(offset));
        SyncSchedule.NextStaggeredSlot(tenantId, hour.AddMinutes(offset)).Should().Be(hour.AddMinutes(offset));
        SyncSchedule.NextStaggeredSlot(tenantId, hour.AddMinutes(offset).AddSeconds(1)).Should().Be(hour.AddHours(1).AddMinutes(offset));
    }

    [Fact]
    public void Every_slot_is_in_the_future_and_within_an_hour()
    {
        var now = new DateTimeOffset(2026, 9, 17, 10, 37, 12, TimeSpan.Zero);

        foreach (var _ in Enumerable.Range(0, 200))
        {
            var slot = SyncSchedule.NextStaggeredSlot(Guid.NewGuid(), now);
            slot.Should().BeOnOrAfter(now);
            slot.Should().BeBefore(now.AddHours(1));
        }
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

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(9, 60)]
    public void Retry_backoff_doubles_and_is_capped(int failedAttempt, int expectedMinutes)
    {
        SyncSchedule.RetryDelay(failedAttempt).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void A_message_id_identifies_one_job_for_one_tenant_and_one_key()
    {
        var tenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var key = SyncSchedule.SlotKey(new DateTimeOffset(2026, 9, 2, 10, 15, 0, TimeSpan.Zero));

        var first = new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, key, "corr-1");
        var second = new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, key, "corr-2");

        second.MessageId.Should().Be(first.MessageId, "a different correlation id is still the same unit of work");
        first.MessageId.Should().Be("11111111-2222-3333-4444-555555555555:LicenseSkuSync:slot:202609021015");
    }

    [Fact]
    public void A_later_slot_is_a_different_unit_of_work()
    {
        var tenantId = Guid.NewGuid();
        var slot = new DateTimeOffset(2026, 9, 2, 10, 15, 0, TimeSpan.Zero);

        var first = new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, SyncSchedule.SlotKey(slot), "c");
        var later = new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, SyncSchedule.SlotKey(slot.AddHours(4)), "c");

        later.MessageId.Should().NotBe(first.MessageId);
    }

    [Fact]
    public void The_session_id_is_the_tenant_so_ordering_holds_within_a_tenant()
    {
        var tenantId = Guid.NewGuid();

        new SyncJobMessage(tenantId, SyncJobType.LicenseSkuSync, "k", "c").SessionId.Should().Be(tenantId.ToString("D"));
    }

    [Fact]
    public void Message_ids_never_exceed_the_service_bus_limit()
    {
        var message = new SyncJobMessage(Guid.NewGuid(), SyncJobType.AssociatedTenantDiscovery, new string('x', 300), "c");

        message.MessageId.Length.Should().BeLessThanOrEqualTo(SyncJobMessage.MaxMessageIdLength);
        message.MessageId.Should().Be(message.MessageId, "the hashed id is stable");
    }

    [Fact]
    public void Retries_and_throttles_get_distinct_ids_and_counters()
    {
        var at = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var original = new SyncJobMessage(Guid.NewGuid(), SyncJobType.CapabilityDiscovery, "slot:1", "c");

        var retry = original.ForRetry(at);
        var retry2 = retry.ForRetry(at);
        var throttled = retry.ForThrottle(at);

        retry.Attempt.Should().Be(2);
        retry2.Attempt.Should().Be(3);
        retry2.DeduplicationKey.Should().Be("slot:1:retry:3");
        throttled.Attempt.Should().Be(2);
        throttled.ThrottleCount.Should().Be(1);
        new[] { original.MessageId, retry.MessageId, retry2.MessageId, throttled.MessageId }.Should().OnlyHaveUniqueItems();
        retry.NotBeforeUtc.Should().Be(at);
    }

    [Fact]
    public void The_broker_message_carries_session_schedule_and_correlation()
    {
        var notBefore = new DateTimeOffset(2026, 9, 17, 10, 42, 0, TimeSpan.Zero);
        var message = new SyncJobMessage(Guid.NewGuid(), SyncJobType.ConsentVerification, "consent:n1", "corr-9") { NotBeforeUtc = notBefore };

        var sent = ServiceBusSyncJobEnqueuer.ToServiceBusMessage(message);

        sent.SessionId.Should().Be(message.TenantId.ToString("D"));
        sent.MessageId.Should().Be(message.MessageId);
        sent.ScheduledEnqueueTime.Should().Be(notBefore);
        sent.CorrelationId.Should().Be("corr-9");
        sent.Subject.Should().Be("ConsentVerification");
        sent.ApplicationProperties[ServiceBusSyncJobEnqueuer.AttemptProperty].Should().Be(1);
    }

    [Theory]
    [InlineData("consent:abc", "consent:abc", 1)]
    [InlineData("consent:abc:attempt:2", "consent:abc", 2)]
    [InlineData("consent:abc:attempt:3", "consent:abc", 3)]
    public void Consent_verification_attempts_travel_in_the_key(string key, string root, int attempt)
    {
        ConsentVerificationAttempts.Parse(key).Should().Be((root, attempt));
        ConsentVerificationAttempts.KeyFor(root, attempt + 1).Should().Be($"{root}:attempt:{attempt + 1}");
    }

    [Fact]
    public void Reconsent_probe_keys_identify_the_due_probe()
    {
        var tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var due = new DateTimeOffset(2026, 9, 17, 11, 7, 30, TimeSpan.Zero);

        ReconsentProbeSchedulePass.DeduplicationKey(tenant, due)
            .Should().Be("reconsent-probe:11111111-2222-3333-4444-555555555555:202609171105", "the web host uses 5-minute slots");
    }
}

/// <summary>The consumer's decisions: gating, consent verification, re-probes, retry and throttle.</summary>
public class SyncJobProcessorTests
{
    private readonly FakeTimeProvider _clock = new(ProbeFixtures.Now);
    private readonly FakeGraphProbe _graph = new();
    private readonly FakeConsentVerifier _verifier = new();
    private readonly RecordingEnqueuer _queue = new();

    private SyncJobProcessor Processor(ITenantOnboardingStore store, CapabilityDiscoveryService? discovery = null)
        => new(
            store,
            discovery ?? new CapabilityDiscoveryService(store, _graph, new FakeArmProbe(), new FakeBillingProbe(), _clock, NullLogger<CapabilityDiscoveryService>.Instance),
            _verifier,
            _queue,
            _queue,
            _clock,
            NullLogger<SyncJobProcessor>.Instance);

    private static SyncJobMessage Job(Tenant tenant, SyncJobType type, string key = "k", int attempt = 1)
        => new(tenant.TenantId, type, key, "corr") { Attempt = attempt };

    [Theory]
    [InlineData(SyncJobType.CapabilityDiscovery, TenantStatus.Active, true)]
    [InlineData(SyncJobType.CapabilityDiscovery, TenantStatus.Provisioning, true)]
    [InlineData(SyncJobType.CapabilityDiscovery, TenantStatus.NeedsReconsent, false)]
    [InlineData(SyncJobType.CapabilityDiscovery, TenantStatus.GracePeriod, false)]
    [InlineData(SyncJobType.CapabilityDiscovery, TenantStatus.NotConnected, false)]
    [InlineData(SyncJobType.LicenseSkuSync, TenantStatus.ConsentPendingVerification, false)]
    [InlineData(SyncJobType.ReconsentProbe, TenantStatus.NeedsReconsent, true)]
    [InlineData(SyncJobType.ReconsentProbe, TenantStatus.Active, false)]
    [InlineData(SyncJobType.ConsentVerification, TenantStatus.ConsentPendingVerification, true)]
    [InlineData(SyncJobType.ConsentVerification, TenantStatus.NeedsReconsent, true)]
    [InlineData(SyncJobType.ConsentVerification, TenantStatus.Active, false)]
    [InlineData(SyncJobType.ConsentVerification, TenantStatus.GracePeriod, false)]
    [InlineData(SyncJobType.TenantDeletion, TenantStatus.GracePeriod, true)]
    public void Jobs_are_gated_by_tenant_status(SyncJobType job, TenantStatus status, bool allowed)
    {
        SyncJobProcessor.IsAllowed(job, status).Should().Be(allowed);
    }

    [Fact]
    public async Task A_job_for_an_ineligible_tenant_is_completed_without_calling_Microsoft()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        tenant.MarkNeedsReconsent("GrantRevoked", ProbeFixtures.Now);

        var result = await Processor(new InMemoryOnboardingStore(tenant))
            .ProcessAsync(Job(tenant, SyncJobType.CapabilityDiscovery), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        result.Reason.Should().Contain("Skipped");
        _graph.Calls.Should().Be(0);
        _queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Verified_consent_confirms_the_tenant_and_enqueues_discovery()
    {
        var tenant = Tenant.Register(Guid.NewGuid(), "Contoso", null, "westeurope", ProbeFixtures.Now);
        tenant.AwaitConsentVerification(Guid.NewGuid(), ProbeFixtures.Now);
        var store = new InMemoryOnboardingStore(tenant);
        _verifier.Result = new ConsentVerificationResult(ConsentVerificationStatus.Verified);

        var result = await Processor(store).ProcessAsync(Job(tenant, SyncJobType.ConsentVerification, "consent:n1"), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        tenant.Status.Should().Be(TenantStatus.Provisioning);
        store.SaveCount.Should().Be(1);
        var audit = store.Audits.Should().ContainSingle().Subject;
        audit.Outcome.Should().Be(Mlcp.Domain.Audit.AuditOutcome.Succeeded);
        audit.CorrelationId.Should().Be("corr");
        audit.NewValue.Should().Be(nameof(TenantStatus.Provisioning));
        _queue.Enqueued.Should().ContainSingle(m => m.JobType == SyncJobType.CapabilityDiscovery && m.DeduplicationKey == "consent:consent:n1");
    }

    [Theory]
    [InlineData("consent:n1", "consent:n1:attempt:2", 5)]
    [InlineData("consent:n1:attempt:2", "consent:n1:attempt:3", 15)]
    public async Task Pending_propagation_is_retried_at_five_then_fifteen_minutes(string key, string nextKey, int minutes)
    {
        var tenant = Tenant.Register(Guid.NewGuid(), "Contoso", null, "westeurope", ProbeFixtures.Now);
        tenant.AwaitConsentVerification(Guid.NewGuid(), ProbeFixtures.Now);
        _verifier.Result = new ConsentVerificationResult(ConsentVerificationStatus.PendingPropagation);

        var result = await Processor(new InMemoryOnboardingStore(tenant))
            .ProcessAsync(Job(tenant, SyncJobType.ConsentVerification, key), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        var next = _queue.Enqueued.Should().ContainSingle().Subject;
        next.JobType.Should().Be(SyncJobType.ConsentVerification);
        next.DeduplicationKey.Should().Be(nextKey);
        next.NotBeforeUtc.Should().Be(ProbeFixtures.Now.AddMinutes(minutes));
        tenant.Status.Should().Be(TenantStatus.ConsentPendingVerification);
    }

    [Fact]
    public async Task Pending_propagation_stops_after_three_attempts()
    {
        var tenant = Tenant.Register(Guid.NewGuid(), "Contoso", null, "westeurope", ProbeFixtures.Now);
        tenant.AwaitConsentVerification(Guid.NewGuid(), ProbeFixtures.Now);
        _verifier.Result = new ConsentVerificationResult(ConsentVerificationStatus.PendingPropagation);

        var result = await Processor(new InMemoryOnboardingStore(tenant))
            .ProcessAsync(Job(tenant, SyncJobType.ConsentVerification, "consent:n1:attempt:3"), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        _queue.Enqueued.Should().BeEmpty();
        tenant.Status.Should().Be(TenantStatus.ConsentPendingVerification);
    }

    [Fact]
    public async Task Not_granted_leaves_the_tenant_alone()
    {
        var tenant = Tenant.Register(Guid.NewGuid(), "Contoso", null, "westeurope", ProbeFixtures.Now);
        tenant.AwaitConsentVerification(Guid.NewGuid(), ProbeFixtures.Now);
        var store = new InMemoryOnboardingStore(tenant);
        _verifier.Result = new ConsentVerificationResult(ConsentVerificationStatus.NotGranted, "AADSTS700016");

        var result = await Processor(store).ProcessAsync(Job(tenant, SyncJobType.ConsentVerification, "consent:n1"), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        tenant.Status.Should().Be(TenantStatus.ConsentPendingVerification);
        store.Audits.Should().ContainSingle().Which.Outcome.Should().Be(Mlcp.Domain.Audit.AuditOutcome.Failed);
        _queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_successful_reprobe_restores_the_tenant_and_enqueues_discovery()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        tenant.MarkNeedsReconsent("FloorPermissionRemoved", ProbeFixtures.Now);
        _graph.Floor = () => ProbeCallOutcome.Success;

        var result = await Processor(new InMemoryOnboardingStore(tenant))
            .ProcessAsync(Job(tenant, SyncJobType.ReconsentProbe), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        tenant.Status.Should().Be(TenantStatus.Active);
        _queue.Enqueued.Should().ContainSingle(m => m.JobType == SyncJobType.CapabilityDiscovery);
    }

    [Fact]
    public async Task An_inconclusive_discovery_is_requeued_rather_than_abandoned()
    {
        var tenant = ProbeFixtures.ProvisioningTenant();
        _graph.Result = () => GraphProbeResult.Inconclusive(ProbeFixtures.Transient503);

        var result = await Processor(new InMemoryOnboardingStore(tenant))
            .ProcessAsync(Job(tenant, SyncJobType.CapabilityDiscovery, "slot:1"), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        var retry = _queue.Requeued.Should().ContainSingle().Subject;
        retry.Attempt.Should().Be(2);
        retry.NotBeforeUtc.Should().Be(ProbeFixtures.Now + CapabilityDiscoveryService.DefaultRetryAfter);
    }

    [Fact]
    public async Task Throttling_completes_and_requeues_at_the_hinted_time()
    {
        var tenant = ProbeFixtures.ActiveTenant();

        // Probes never throw into the processor (discovery turns that into an inconclusive
        // verdict), so the throttle is raised from the job's first step instead.
        var store = new ThrowingStore(tenant, new MicrosoftThrottledException(TimeSpan.FromMinutes(7)));

        var result = await Processor(store).ProcessAsync(Job(tenant, SyncJobType.CapabilityDiscovery, "slot:1"), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        var later = _queue.Requeued.Should().ContainSingle().Subject;
        later.NotBeforeUtc.Should().Be(ProbeFixtures.Now.AddMinutes(7));
        later.ThrottleCount.Should().Be(1);
        later.Attempt.Should().Be(1, "throttling is not a failed attempt");
    }

    [Fact]
    public async Task Failures_retry_with_backoff_then_dead_letter_after_five_attempts()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new ThrowingStore(tenant, new InvalidOperationException("database down"));

        var early = await Processor(store).ProcessAsync(Job(tenant, SyncJobType.CapabilityDiscovery, "slot:1", attempt: 2), CancellationToken.None);
        early.Disposition.Should().Be(SyncJobDisposition.Complete);
        _queue.Requeued.Should().ContainSingle().Which.NotBeforeUtc.Should().Be(ProbeFixtures.Now.AddMinutes(4));

        var last = await Processor(store).ProcessAsync(Job(tenant, SyncJobType.CapabilityDiscovery, "slot:1", attempt: 5), CancellationToken.None);
        last.Disposition.Should().Be(SyncJobDisposition.DeadLetter);
        _queue.Requeued.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_refusal_is_completed_not_retried()
    {
        var tenant = ProbeFixtures.ActiveTenant();
        var store = new ThrowingStore(tenant, new MicrosoftCallRefusedException("denied"));

        var result = await Processor(store).ProcessAsync(Job(tenant, SyncJobType.CapabilityDiscovery), CancellationToken.None);

        result.Disposition.Should().Be(SyncJobDisposition.Complete);
        _queue.Requeued.Should().BeEmpty();
    }

    private sealed class ThrowingStore : ITenantOnboardingStore
    {
        private readonly Tenant _tenant;
        private readonly Exception _exception;

        public ThrowingStore(Tenant tenant, Exception exception)
        {
            _tenant = tenant;
            _exception = exception;
        }

        public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
            => throw _exception;

        public Task<Mlcp.Domain.Capabilities.TenantCapabilityProfile?> FindCapabilityProfileAsync(Guid tenantId, CancellationToken cancellationToken)
            => Task.FromResult<Mlcp.Domain.Capabilities.TenantCapabilityProfile?>(null);

        public Task AddCapabilityProfileAsync(Mlcp.Domain.Capabilities.TenantCapabilityProfile profile, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<OnboardingStep> GetOrCreateStepAsync(Guid tenantId, OnboardingStepName step, CancellationToken cancellationToken)
            => Task.FromResult(OnboardingStep.Pending(_tenant.TenantId, step, ProbeFixtures.Now));

        public Task AddAuditAsync(Mlcp.Domain.Audit.AuditLog entry, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
