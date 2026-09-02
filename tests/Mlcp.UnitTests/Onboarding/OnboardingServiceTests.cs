using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Onboarding;

/// <summary>
/// P0-6: an analyst signs in, a request reaches an admin, the admin's link completes, and the
/// tenant becomes connected. These pin the decisions that are easy to get subtly wrong — who
/// becomes an Owner, what happens on a repeat request, and which tenant a consent applies to.
/// </summary>
public class OnboardingServiceTests
{
    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid AnalystId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private static SignedInUser Analyst => new(TenantA, AnalystId, "analyst@contoso.example", "Ann Analyst");

    private static SignedInUser Admin => new(TenantA, AdminId, "admin@contoso.example", "Adam Admin");

    private sealed class FakeEmailSender : IConsentEmailSender
    {
        public List<(PendingConsentRequest Request, string Url)> Sent { get; } = [];

        public Task SendConsentRequestAsync(PendingConsentRequest request, string url, CancellationToken ct)
        {
            Sent.Add((request, url));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRepository : IOnboardingRepository
    {
        public List<Tenant> Tenants { get; } = [];

        public List<AppUser> Users { get; } = [];

        public List<PendingConsentRequest> Requests { get; } = [];

        public List<AuditLog> Audits { get; } = [];

        public int SaveCount { get; private set; }

        public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken ct)
            => Task.FromResult(Tenants.Find(t => t.TenantId == tenantId));

        public Task AddTenantAsync(Tenant tenant, CancellationToken ct)
        {
            Tenants.Add(tenant);
            return Task.CompletedTask;
        }

        public Task<AppUser?> FindAppUserAsync(Guid tenantId, Guid objectId, CancellationToken ct)
            => Task.FromResult(Users.Find(u => u.TenantId == tenantId && u.EntraObjectId == objectId));

        public Task<bool> HasAnyAppUserAsync(Guid tenantId, CancellationToken ct)
            => Task.FromResult(Users.Exists(u => u.TenantId == tenantId));

        public Task AddAppUserAsync(AppUser user, CancellationToken ct)
        {
            Users.Add(user);
            return Task.CompletedTask;
        }

        public Task<PendingConsentRequest?> FindOpenConsentRequestAsync(Guid tenantId, CancellationToken ct)
            => Task.FromResult(Requests.Find(r => r.TenantId == tenantId && r.CompletedUtc is null));

        public Task<PendingConsentRequest?> FindConsentRequestByTokenAsync(string token, CancellationToken ct)
            => Task.FromResult(Requests.Find(r => r.Token == token));

        public Task AddConsentRequestAsync(PendingConsentRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public Task AddAuditAsync(AuditLog entry, CancellationToken ct)
        {
            Audits.Add(entry);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private static (OnboardingService Service, InMemoryRepository Repo, FakeEmailSender Email, FakeTimeProvider Clock) Build()
    {
        var repo = new InMemoryRepository();
        var email = new FakeEmailSender();
        var clock = new FakeTimeProvider(Start);

        return (
            new OnboardingService(repo, email, clock, NullLogger<OnboardingService>.Instance),
            repo,
            email,
            clock);
    }

    [Fact]
    public async Task A_first_sign_in_registers_the_tenant_but_does_not_connect_it()
    {
        var (service, repo, _, _) = Build();

        var state = await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);

        repo.Tenants.Should().ContainSingle();
        state.IsConnected.Should().BeFalse("registering is not consenting");
        state.Tenant!.ConsentGrantedUtc.Should().BeNull();
    }

    [Fact]
    public async Task The_first_user_in_a_tenant_becomes_owner()
    {
        // Somebody has to be able to grant roles, and there is nobody yet to grant them.
        var (service, repo, _, _) = Build();

        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);

        repo.Users.Should().ContainSingle().Which.Role.Should().Be(AppRole.Owner);
    }

    [Fact]
    public async Task A_later_colleague_does_not_inherit_owner()
    {
        // An unknown colleague from the same directory is a legitimate user of the product but
        // not automatically an administrator of it.
        var (service, repo, _, _) = Build();

        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);
        await service.RecordSignInAsync(Admin, "westeurope", CancellationToken.None);

        repo.Users.Should().HaveCount(2);
        repo.Users.Find(u => u.EntraObjectId == AdminId)!.Role.Should().Be(AppRole.Viewer);
    }

    [Fact]
    public async Task Signing_in_twice_does_not_create_a_second_tenant_or_user()
    {
        var (service, repo, _, _) = Build();

        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);

        repo.Tenants.Should().ContainSingle();
        repo.Users.Should().ContainSingle();
    }

    [Fact]
    public async Task Requesting_consent_emails_a_link_and_writes_an_audit_row()
    {
        var (service, repo, email, _) = Build();
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);

        await service.RequestAdminConsentAsync(
            Analyst,
            "admin@contoso.example",
            r => $"https://mlcp.example/onboarding/consent/{r.Token}",
            CancellationToken.None);

        email.Sent.Should().ContainSingle();
        email.Sent[0].Url.Should().Contain(repo.Requests[0].Token);
        repo.Audits.Should().ContainSingle().Which.Action.Should().Be(AuditAction.ConsentRequested);
    }

    [Fact]
    public async Task Asking_again_reuses_the_existing_token()
    {
        // Minting a new token would invalidate the link in an email the admin may be about to
        // click, and would let a user generate unlimited valid tokens.
        var (service, repo, email, clock) = Build();
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);

        await service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);
        var firstToken = repo.Requests[0].Token;

        clock.Advance(TimeSpan.FromMinutes(5));

        await service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        repo.Requests.Should().ContainSingle();
        repo.Requests[0].Token.Should().Be(firstToken);
        repo.Requests[0].SendCount.Should().Be(2);
        email.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task Resending_extends_the_expiry()
    {
        var (service, repo, _, clock) = Build();
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);
        await service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        var firstExpiry = repo.Requests[0].ExpiresUtc;
        clock.Advance(TimeSpan.FromDays(3));

        await service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        repo.Requests[0].ExpiresUtc.Should().BeAfter(firstExpiry);
    }

    [Fact]
    public async Task An_expired_request_is_replaced_rather_than_resent()
    {
        var (service, repo, _, clock) = Build();
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);
        await service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        var firstToken = repo.Requests[0].Token;
        clock.Advance(OnboardingService.ConsentRequestLifetime + TimeSpan.FromDays(1));

        await service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        repo.Requests.Should().HaveCount(2);
        repo.Requests[1].Token.Should().NotBe(firstToken);
    }

    [Fact]
    public async Task Completing_consent_connects_the_tenant_and_closes_the_request()
    {
        var (service, repo, _, _) = Build();
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);
        await service.RequestAdminConsentAsync(Analyst, "admin@contoso.example", r => r.Token, CancellationToken.None);

        await service.CompleteAdminConsentAsync(Admin, "westeurope", CancellationToken.None);

        repo.Tenants[0].ConsentGrantedUtc.Should().NotBeNull();
        repo.Tenants[0].Status.Should().Be(TenantStatus.Provisioning, "discovery has not run yet");
        repo.Requests[0].CompletedUtc.Should().NotBeNull();
        repo.Audits.Should().Contain(a => a.Action == AuditAction.ConsentGranted);
    }

    [Fact]
    public async Task The_consenting_administrator_is_promoted_to_owner()
    {
        // They can administer the tenant at Microsoft, so withholding it here would be theatre.
        var (service, repo, _, _) = Build();
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);
        await service.RecordSignInAsync(Admin, "westeurope", CancellationToken.None);

        await service.CompleteAdminConsentAsync(Admin, "westeurope", CancellationToken.None);

        repo.Users.Find(u => u.EntraObjectId == AdminId)!.Role.Should().Be(AppRole.Owner);
    }

    [Fact]
    public async Task Consent_arriving_twice_leaves_the_same_state()
    {
        // Callbacks get replayed and admins refresh pages.
        var (service, repo, _, _) = Build();
        await service.CompleteAdminConsentAsync(Admin, "westeurope", CancellationToken.None);
        await service.CompleteAdminConsentAsync(Admin, "westeurope", CancellationToken.None);

        repo.Tenants.Should().ContainSingle();
        repo.Users.Should().ContainSingle();
    }

    [Fact]
    public async Task Disconnecting_schedules_deletion_thirty_days_out()
    {
        var (service, repo, _, _) = Build();
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);
        await service.CompleteAdminConsentAsync(Admin, "westeurope", CancellationToken.None);

        var tenant = await service.DisconnectAsync(Admin, CancellationToken.None);

        tenant.Status.Should().Be(TenantStatus.GracePeriod);
        tenant.DeleteScheduledUtc.Should().Be(Start + OnboardingService.DeletionGracePeriod);
        repo.Audits.Should().Contain(a => a.Action == AuditAction.TenantDisconnected);
    }

    [Fact]
    public async Task A_disconnect_can_be_reversed_inside_the_grace_period()
    {
        var (service, _, _, clock) = Build();
        await service.RecordSignInAsync(Analyst, "westeurope", CancellationToken.None);
        await service.CompleteAdminConsentAsync(Admin, "westeurope", CancellationToken.None);

        var tenant = await service.DisconnectAsync(Admin, CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(5));

        tenant.CancelDeletion(clock.GetUtcNow());

        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.DeleteScheduledUtc.Should().BeNull();
    }
}
