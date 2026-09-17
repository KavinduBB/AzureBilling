using FluentAssertions;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Onboarding;

/// <summary>
/// P0-6 under ADR-018: who is Owner, how the consent callback is verified rather than trusted,
/// and what the destructive actions require.
/// </summary>
public class OnboardingServiceTests
{
    private const string Region = OnboardingHarness.Region;

    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid AnalystId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private static SignedInUser Analyst => new(TenantA, AnalystId, "analyst@contoso.example", "Ann Analyst");

    private static SignedInUser Admin => new(TenantA, AdminId, "admin@contoso.example", "Adam Admin", IsDirectoryAdmin: true);

    private static SignedInUser AdminWithoutRole => Admin with { IsDirectoryAdmin = false };

    // ---------------------------------------------------------------- sign-in and Owner

    [Fact]
    public async Task A_first_sign_in_registers_the_tenant_as_not_connected()
    {
        var h = new OnboardingHarness();

        var state = await h.Service.RecordSignInAsync(Analyst, Region, CancellationToken.None);

        h.Repository.Tenants.Should().ContainSingle();
        state.Tenant!.Status.Should().Be(TenantStatus.NotConnected);
        state.IsConnected.Should().BeFalse("registering is not consenting");
        state.Tenant.ConsentGrantedUtc.Should().BeNull();
    }

    [Fact]
    public async Task The_first_person_to_sign_in_is_not_owner_unless_they_are_a_directory_admin()
    {
        // ADR-018: arriving first is not a qualification for deleting the organisation's data.
        var h = new OnboardingHarness();

        var state = await h.Service.RecordSignInAsync(Analyst, Region, CancellationToken.None);

        state.Role.Should().Be(AppRole.Viewer);
        h.User(AnalystId).Role.Should().Be(AppRole.Viewer);
    }

    [Fact]
    public async Task A_directory_admin_is_owner_from_their_first_sign_in()
    {
        var h = new OnboardingHarness();

        await h.Service.RecordSignInAsync(Analyst, Region, CancellationToken.None);
        var state = await h.Service.RecordSignInAsync(Admin, Region, CancellationToken.None);

        state.Role.Should().Be(AppRole.Owner);
        h.User(AdminId).Role.Should().Be(AppRole.Owner);
    }

    [Fact]
    public async Task An_admin_who_loses_the_directory_role_loses_owner_at_the_next_sign_in()
    {
        var h = new OnboardingHarness();
        await h.Service.RecordSignInAsync(Admin, Region, CancellationToken.None);

        var state = await h.Service.RecordSignInAsync(AdminWithoutRole, Region, CancellationToken.None);

        state.Role.Should().Be(AppRole.Viewer);
        h.Repository.Audits.Should().ContainSingle(a => a.Action == AuditAction.AppUserRoleChanged)
            .Which.NewValue.Should().Contain("Viewer");
    }

    [Fact]
    public async Task A_colleague_who_becomes_an_admin_is_upgraded_and_the_change_is_audited()
    {
        var h = new OnboardingHarness();
        await h.Service.RecordSignInAsync(AdminWithoutRole, Region, CancellationToken.None);

        await h.Service.RecordSignInAsync(Admin, Region, CancellationToken.None);

        h.User(AdminId).Role.Should().Be(AppRole.Owner);
        var audit = h.Repository.Audits.Should().ContainSingle(a => a.Action == AuditAction.AppUserRoleChanged).Subject;
        audit.OldValue.Should().Contain("Viewer");
        audit.NewValue.Should().Contain("Owner").And.Contain("wids");
    }

    [Fact]
    public async Task An_unchanged_role_writes_no_audit_row()
    {
        var h = new OnboardingHarness();

        await h.Service.RecordSignInAsync(Admin, Region, CancellationToken.None);
        await h.Service.RecordSignInAsync(Admin, Region, CancellationToken.None);

        h.Repository.Audits.Should().NotContain(a => a.Action == AuditAction.AppUserRoleChanged);
    }

    [Fact]
    public async Task Signing_in_twice_does_not_create_a_second_tenant_or_user()
    {
        var h = new OnboardingHarness();

        await h.Service.RecordSignInAsync(Analyst, Region, CancellationToken.None);
        await h.Service.RecordSignInAsync(Analyst, Region, CancellationToken.None);

        h.Repository.Tenants.Should().ContainSingle();
        h.Repository.Users.Should().ContainSingle();
    }

    [Fact]
    public async Task Signing_in_does_not_claim_a_region_in_the_global_directory()
    {
        // A colleague's sign-in must not decide where the organisation's data lives.
        var h = new OnboardingHarness();

        await h.Service.RecordSignInAsync(Analyst, Region, CancellationToken.None);

        h.Regions.Regions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- connect

    [Fact]
    public async Task A_non_admin_cannot_start_admin_consent()
    {
        var h = new OnboardingHarness();

        var outcome = await h.Service.BeginConnectAsync(Analyst, Region, "corr", CancellationToken.None);

        outcome.Decision.Should().Be(ConnectDecision.NotDirectoryAdmin);
        h.Regions.Regions.Should().BeEmpty();
        h.Repository.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task Connecting_claims_the_region_and_audits_the_confirmation_before_leaving_for_entra()
    {
        var h = new OnboardingHarness();

        var outcome = await h.Service.BeginConnectAsync(Admin, Region, "corr-1", CancellationToken.None);

        outcome.Decision.Should().Be(ConnectDecision.Proceed);
        h.Regions.Regions[TenantA].Should().Be(Region);

        var audit = h.Repository.Audits.Should().ContainSingle().Subject;
        audit.Action.Should().Be(AuditAction.ConsentGranted);
        audit.Outcome.Should().Be(AuditOutcome.Attempted);
        audit.CorrelationId.Should().Be("corr-1");
        audit.NewValue.Should().Contain("\"region\":\"eu\"").And.Contain("\"regionConfirmed\":true");
    }

    [Fact]
    public async Task A_tenant_registered_in_another_region_is_sent_there()
    {
        var h = new OnboardingHarness();
        h.Regions.Regions[TenantA] = "us";

        var outcome = await h.Service.BeginConnectAsync(Admin, Region, "corr", CancellationToken.None);

        outcome.Should().Be(new ConnectOutcome(ConnectDecision.RegisteredElsewhere, "us"));
        h.Repository.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disconnected_tenant_cannot_start_consent_until_the_disconnect_is_cancelled()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        await h.Service.DisconnectAsync(Admin, CancellationToken.None);

        var outcome = await h.Service.BeginConnectAsync(Admin, Region, "corr", CancellationToken.None);

        outcome.Decision.Should().Be(ConnectDecision.Disconnected);
    }

    // ---------------------------------------------------------------- consent callback

    [Fact]
    public async Task A_verified_callback_confirms_consent_and_queues_discovery_without_running_it()
    {
        var h = new OnboardingHarness();
        await h.Service.RecordSignInAsync(Analyst, Region, CancellationToken.None);
        h.DirectoryInfo.Domains = ["contoso.example"];
        await h.Service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        var result = await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr-2", CancellationToken.None);

        result.Should().Be(ConsentCallbackResult.Verified);
        h.Verifier.Calls.Should().Be(1, "the one permitted in-request Microsoft call");
        h.Tenant(TenantA).Status.Should().Be(TenantStatus.Provisioning, "discovery runs in the worker");
        h.Tenant(TenantA).ConsentGrantedUtc.Should().NotBeNull();
        h.Repository.Requests.Single().CompletedUtc.Should().NotBeNull();

        h.Jobs.Jobs.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            TenantId = TenantA,
            JobType = SyncJobType.CapabilityDiscovery,
            NotBeforeUtc = (DateTimeOffset?)null,
            CorrelationId = "corr-2",
        });

        h.Repository.Audits.Should().Contain(a =>
            a.Action == AuditAction.ConsentGranted && a.Outcome == AuditOutcome.Succeeded && a.CorrelationId == "corr-2");
    }

    [Fact]
    public async Task A_callback_during_propagation_queues_verification_two_minutes_out()
    {
        var h = new OnboardingHarness();
        h.Verifier.Next = ConsentVerificationStatus.PendingPropagation;

        var result = await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr", CancellationToken.None);

        result.Should().Be(ConsentCallbackResult.PendingVerification);
        h.Tenant(TenantA).Status.Should().Be(TenantStatus.ConsentPendingVerification);
        h.Tenant(TenantA).ConsentGrantedUtc.Should().BeNull("consent is not assumed");

        var job = h.Jobs.Jobs.Should().ContainSingle().Subject;
        job.JobType.Should().Be(SyncJobType.ConsentVerification);
        job.NotBeforeUtc.Should().Be(OnboardingHarness.Start + TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task A_callback_that_microsoft_says_is_not_granted_changes_nothing_and_is_audited_as_failed()
    {
        var h = new OnboardingHarness();
        h.Verifier.Next = ConsentVerificationStatus.NotGranted;

        var result = await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr", CancellationToken.None);

        result.Should().Be(ConsentCallbackResult.NotGranted);
        h.Tenant(TenantA).ConsentGrantedUtc.Should().BeNull();
        h.Jobs.Jobs.Should().BeEmpty();
        h.Repository.Audits.Should().ContainSingle(a => a.Outcome == AuditOutcome.Failed);
    }

    [Fact]
    public async Task An_unclassified_verification_failure_concludes_nothing_and_retries()
    {
        var h = new OnboardingHarness();
        h.Verifier.Next = ConsentVerificationStatus.Failed;

        var result = await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr", CancellationToken.None);

        result.Should().Be(ConsentCallbackResult.CouldNotConfirm);
        h.Tenant(TenantA).ConsentGrantedUtc.Should().BeNull();
        h.Jobs.Jobs.Should().ContainSingle().Which.JobType.Should().Be(SyncJobType.ConsentVerification);
    }

    [Fact]
    public async Task A_callback_from_a_non_admin_is_refused_before_any_microsoft_call()
    {
        var h = new OnboardingHarness();

        var result = await h.Service.CompleteAdminConsentAsync(Analyst, Region, "corr", CancellationToken.None);

        result.Should().Be(ConsentCallbackResult.NotDirectoryAdmin);
        h.Verifier.Calls.Should().Be(0);
        h.Repository.Tenants.Should().BeEmpty();
    }

    [Fact]
    public async Task A_callback_never_cancels_a_scheduled_deletion()
    {
        // ADR-018 rule 5: reconnecting during the grace period is the explicit cancel action.
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        var tenant = await h.Service.DisconnectAsync(Admin, CancellationToken.None);
        var scheduled = tenant.DeleteScheduledUtc;

        var result = await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr-again", CancellationToken.None);

        result.Should().Be(ConsentCallbackResult.Disconnected);
        tenant.Status.Should().Be(TenantStatus.GracePeriod);
        tenant.DeleteScheduledUtc.Should().Be(scheduled);
        h.Verifier.Calls.Should().Be(1, "only the original connection was verified");
    }

    [Fact]
    public async Task A_verified_callback_restores_a_tenant_that_needed_reconsent()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        h.Tenant(TenantA).MarkNeedsReconsent("GrantRevoked: test", h.Clock.GetUtcNow());

        var result = await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr-3", CancellationToken.None);

        result.Should().Be(ConsentCallbackResult.Verified);
        h.Tenant(TenantA).Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public async Task A_queue_failure_after_a_verified_consent_does_not_fail_the_callback()
    {
        // The state is durable and the scheduler sweeps Provisioning tenants.
        var h = new OnboardingHarness();
        h.Jobs.Fail = true;

        var result = await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr", CancellationToken.None);

        result.Should().Be(ConsentCallbackResult.Verified);
        h.Tenant(TenantA).Status.Should().Be(TenantStatus.Provisioning);
    }

    [Fact]
    public async Task Consent_arriving_twice_leaves_the_same_state()
    {
        var h = new OnboardingHarness();

        await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr", CancellationToken.None);
        await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr", CancellationToken.None);

        h.Repository.Tenants.Should().ContainSingle();
        h.Tenant(TenantA).Status.Should().Be(TenantStatus.Provisioning);
    }

    // ---------------------------------------------------------------- consent request emails

    [Fact]
    public async Task Requesting_consent_emails_a_link_and_writes_an_audit_row()
    {
        var h = new OnboardingHarness();
        await h.Service.RecordSignInAsync(Analyst, Region, CancellationToken.None);

        var outcome = await h.Service.RequestAdminConsentAsync(
            Analyst,
            "admin@contoso.example",
            r => $"https://eu.mlcp.example/onboarding/consent/{r.Token}",
            CancellationToken.None);

        outcome.IsSent.Should().BeTrue();
        h.Email.Sent.Should().ContainSingle();
        h.Email.Sent[0].Url.Should().Contain(h.Repository.Requests[0].Token);
        h.Repository.Audits.Should().ContainSingle(a => a.Action == AuditAction.ConsentRequested)
            .Which.NewValue.Should().NotContain("admin@", "the recipient address is not copied into the audit trail");
    }

    [Fact]
    public async Task Asking_the_same_admin_again_after_the_cooldown_reuses_the_token()
    {
        var h = new OnboardingHarness();

        await h.Service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);
        var firstToken = h.Repository.Requests[0].Token;
        var firstExpiry = h.Repository.Requests[0].ExpiresUtc;

        h.Clock.Advance(TimeSpan.FromMinutes(16));
        var outcome = await h.Service.RequestAdminConsentAsync(Analyst, "ADMIN@contoso.example", r => r.Token, CancellationToken.None);

        outcome.IsSent.Should().BeTrue();
        h.Repository.Requests.Should().ContainSingle();
        h.Repository.Requests[0].Token.Should().Be(firstToken);
        h.Repository.Requests[0].SendCount.Should().Be(2);
        h.Repository.Requests[0].ExpiresUtc.Should().BeAfter(firstExpiry);
        h.Email.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task Asking_a_different_address_creates_a_new_request()
    {
        var h = new OnboardingHarness();

        await h.Service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromMinutes(16));
        await h.Service.RequestAdminConsentAsync(Analyst, "other.admin@contoso.example", r => r.Token, CancellationToken.None);

        h.Repository.Requests.Should().HaveCount(2);
        h.Repository.Requests.Select(r => r.SentToEmail).Should().Equal("admin@contoso.example", "other.admin@contoso.example");
        h.Repository.Requests[0].Token.Should().NotBe(h.Repository.Requests[1].Token);
        h.Repository.Requests[0].SentToEmail.Should().Be("admin@contoso.example", "an existing request is never re-pointed");
    }

    [Fact]
    public async Task A_second_request_inside_the_cooldown_is_refused()
    {
        var h = new OnboardingHarness();

        await h.Service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var outcome = await h.Service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        outcome.Refusal.Should().Be(ConsentRequestRefusal.Cooldown);
        outcome.RetryAfterUtc.Should().Be(OnboardingHarness.Start + TimeSpan.FromMinutes(15));
        h.Email.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task One_person_can_send_three_requests_a_day()
    {
        var h = new OnboardingHarness();

        for (var i = 0; i < 3; i++)
        {
            (await h.Service.RequestAdminConsentAsync(Analyst, $"admin{i}@contoso.example", r => r.Token, CancellationToken.None))
                .IsSent.Should().BeTrue();
            h.Clock.Advance(TimeSpan.FromMinutes(16));
        }

        var fourth = await h.Service.RequestAdminConsentAsync(Analyst, "admin9@contoso.example", r => r.Token, CancellationToken.None);

        fourth.Refusal.Should().Be(ConsentRequestRefusal.UserDailyLimit);
        h.Email.Sent.Should().HaveCount(3);

        h.Clock.Advance(TimeSpan.FromHours(24));
        (await h.Service.RequestAdminConsentAsync(Analyst, "admin9@contoso.example", r => r.Token, CancellationToken.None))
            .IsSent.Should().BeTrue("the window has moved on");
    }

    [Fact]
    public async Task An_organisation_can_send_ten_requests_a_day()
    {
        var h = new OnboardingHarness();

        for (var i = 0; i < 10; i++)
        {
            var colleague = new SignedInUser(TenantA, Guid.NewGuid(), $"user{i}@contoso.example", $"User {i}");
            (await h.Service.RequestAdminConsentAsync(colleague, "admin@contoso.example", r => r.Token, CancellationToken.None))
                .IsSent.Should().BeTrue();
        }

        var eleventh = await h.Service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        eleventh.Refusal.Should().Be(ConsentRequestRefusal.TenantDailyLimit);
        h.Email.Sent.Should().HaveCount(10);
    }

    [Fact]
    public async Task While_verified_domains_are_unknown_only_the_requesters_own_domain_is_allowed()
    {
        var h = new OnboardingHarness();

        var outcome = await h.Service.RequestAdminConsentAsync(Analyst, "someone@evil.example", r => r.Token, CancellationToken.None);

        outcome.Refusal.Should().Be(ConsentRequestRefusal.DomainNotAllowed);
        h.Email.Sent.Should().BeEmpty();
        h.Repository.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Any_verified_domain_of_the_tenant_is_allowed()
    {
        var h = new OnboardingHarness();
        h.DirectoryInfo.Domains = ["contoso.example", "contoso-group.example"];

        var outcome = await h.Service.RequestAdminConsentAsync(Analyst, "it@CONTOSO-group.example", r => r.Token, CancellationToken.None);

        outcome.IsSent.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("Adam <admin@contoso.example>")]
    [InlineData("admin@contoso.example, other@contoso.example")]
    [InlineData("admin@contoso.example;other@contoso.example")]
    [InlineData("admin@contoso")]
    [InlineData("ad min@contoso.example")]
    [InlineData("admin@contoso.example\r\nBcc: x@evil.example")]
    [InlineData("\"quoted\"@contoso.example")]
    public async Task Malformed_addresses_are_refused(string address)
    {
        var h = new OnboardingHarness();

        var outcome = await h.Service.RequestAdminConsentAsync(Analyst, address, r => r.Token, CancellationToken.None);

        outcome.Refusal.Should().Be(ConsentRequestRefusal.InvalidAddress);
        h.Email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_address_longer_than_254_characters_is_refused()
    {
        var h = new OnboardingHarness();
        var longAddress = new string('a', 60) + "@" + string.Join('.', Enumerable.Repeat(new string('b', 60), 4)) + ".example";
        longAddress.Length.Should().BeGreaterThan(254);

        var outcome = await h.Service.RequestAdminConsentAsync(Analyst, longAddress, r => r.Token, CancellationToken.None);

        outcome.Refusal.Should().Be(ConsentRequestRefusal.InvalidAddress);
    }

    [Fact]
    public async Task A_connected_organisation_has_nothing_to_ask_for()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);

        var outcome = await h.Service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        outcome.Refusal.Should().Be(ConsentRequestRefusal.AlreadyConnected);
    }

    // ---------------------------------------------------------------- destructive actions

    [Fact]
    public async Task Disconnecting_requires_a_directory_admin()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);

        var act = () => h.Service.DisconnectAsync(AdminWithoutRole, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        h.Tenant(TenantA).Status.Should().Be(TenantStatus.Provisioning);
    }

    [Fact]
    public async Task Disconnecting_audits_the_attempt_first_and_schedules_deletion_thirty_days_out()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        h.Repository.Audits.Clear();

        var tenant = await h.Service.DisconnectAsync(Admin, CancellationToken.None);

        tenant.Status.Should().Be(TenantStatus.GracePeriod);
        tenant.DeleteScheduledUtc.Should().Be(OnboardingHarness.Start + OnboardingService.DeletionGracePeriod);
        h.Repository.Audits.Select(a => (a.Action, a.Outcome)).Should().Equal(
            (AuditAction.TenantDisconnected, AuditOutcome.Attempted),
            (AuditAction.TenantDisconnected, AuditOutcome.Succeeded));
        h.Repository.Audits.Select(a => a.CorrelationId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Cancelling_the_disconnect_restores_the_tenant_and_requeues_discovery()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        h.Tenant(TenantA).Activate(h.Clock.GetUtcNow());
        await h.Service.DisconnectAsync(Admin, CancellationToken.None);
        h.Jobs.Jobs.Clear();
        h.Clock.Advance(TimeSpan.FromDays(5));

        var tenant = await h.Service.CancelDisconnectAsync(Admin, CancellationToken.None);

        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.DeleteScheduledUtc.Should().BeNull();
        h.Jobs.Jobs.Should().ContainSingle().Which.JobType.Should().Be(SyncJobType.CapabilityDiscovery);
        h.Repository.Audits.Should().Contain(a => a.Action == AuditAction.TenantConnected && a.Outcome == AuditOutcome.Succeeded);
    }

    [Fact]
    public async Task Cancelling_the_disconnect_requires_a_directory_admin()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        await h.Service.DisconnectAsync(Admin, CancellationToken.None);

        var act = () => h.Service.CancelDisconnectAsync(AdminWithoutRole, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        h.Tenant(TenantA).Status.Should().Be(TenantStatus.GracePeriod);
    }

    [Fact]
    public async Task Check_again_queues_a_floor_reprobe_for_a_tenant_that_needs_reconsent()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        h.Tenant(TenantA).MarkNeedsReconsent("GrantRevoked: test", h.Clock.GetUtcNow());
        h.Jobs.Jobs.Clear();

        var queued = await h.Service.RequestReconsentProbeAsync(Admin, CancellationToken.None);
        await h.Service.RequestReconsentProbeAsync(Admin, CancellationToken.None);

        queued.Should().BeTrue();
        h.Jobs.Jobs.Should().HaveCount(2).And.OnlyContain(j => j.JobType == SyncJobType.ReconsentProbe);
        h.Jobs.Jobs.Select(j => j.DeduplicationKey).Distinct().Should().ContainSingle(
            "clicks inside one five-minute slot collapse to one probe");
    }

    [Fact]
    public async Task Check_again_does_nothing_for_a_healthy_tenant()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        h.Jobs.Jobs.Clear();

        var queued = await h.Service.RequestReconsentProbeAsync(Admin, CancellationToken.None);

        queued.Should().BeFalse();
        h.Jobs.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_again_is_owner_only()
    {
        var h = new OnboardingHarness();

        var act = () => h.Service.RequestReconsentProbeAsync(Analyst, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Usage_insights_consent_queues_a_delayed_reprobe_and_concludes_nothing()
    {
        var h = new OnboardingHarness();
        await ConnectAndVerifyAsync(h);
        h.Jobs.Jobs.Clear();

        await h.Service.RecordUsageInsightsConsentAsync(Admin, "corr-ui", CancellationToken.None);

        var job = h.Jobs.Jobs.Should().ContainSingle().Subject;
        job.JobType.Should().Be(SyncJobType.CapabilityDiscovery);
        job.NotBeforeUtc.Should().Be(h.Clock.GetUtcNow() + OnboardingService.ConsentVerificationDelay);
        h.Repository.Audits.Should().Contain(a => a.Action == AuditAction.CapabilityUnlocked && a.Outcome == AuditOutcome.Attempted);
    }

    private static async Task ConnectAndVerifyAsync(OnboardingHarness h)
    {
        (await h.Service.BeginConnectAsync(Admin, Region, "corr-connect", CancellationToken.None))
            .Decision.Should().Be(ConnectDecision.Proceed);
        (await h.Service.CompleteAdminConsentAsync(Admin, Region, "corr-connect", CancellationToken.None))
            .Should().Be(ConsentCallbackResult.Verified);
    }
}
