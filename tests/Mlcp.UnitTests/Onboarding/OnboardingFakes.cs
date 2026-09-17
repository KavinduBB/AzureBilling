using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Onboarding;
using Mlcp.Application.Sync;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Onboarding;

internal sealed class FakeEmailSender : IConsentEmailSender
{
    public List<(PendingConsentRequest Request, string Url)> Sent { get; } = [];

    public Task SendConsentRequestAsync(PendingConsentRequest request, string consentLandingUrl, CancellationToken cancellationToken)
    {
        Sent.Add((request, consentLandingUrl));
        return Task.CompletedTask;
    }
}

internal sealed class FakeConsentVerifier : IConsentVerifier
{
    public ConsentVerificationStatus Next { get; set; } = ConsentVerificationStatus.Verified;

    public int Calls { get; private set; }

    public Task<ConsentVerificationResult> VerifyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new ConsentVerificationResult(Next));
    }
}

internal sealed record EnqueuedJob(Guid TenantId, SyncJobType JobType, string DeduplicationKey, DateTimeOffset? NotBeforeUtc, string CorrelationId);

internal sealed class RecordingJobEnqueuer : ISyncJobEnqueuer
{
    public List<EnqueuedJob> Jobs { get; } = [];

    public bool Fail { get; set; }

    public Task EnqueueAsync(
        Guid tenantId,
        SyncJobType jobType,
        string deduplicationKey,
        DateTimeOffset? notBeforeUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("queue unavailable");
        }

        Jobs.Add(new EnqueuedJob(tenantId, jobType, deduplicationKey, notBeforeUtc, correlationId));
        return Task.CompletedTask;
    }
}

internal sealed class FakeDirectoryInfo : ITenantDirectoryInfo
{
    public IReadOnlyCollection<string>? Domains { get; set; }

    public Task<IReadOnlyCollection<string>?> GetVerifiedDomainsAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Domains);
}

internal sealed class FakeRegionDirectory : IRegionDirectory
{
    public Dictionary<Guid, string> Regions { get; } = [];

    public Task<string?> GetRegionAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Regions.TryGetValue(tenantId, out var region) ? region : null);

    public Task<RegionRegistration> TryRegisterAsync(Guid tenantId, string region, CancellationToken cancellationToken)
    {
        var added = Regions.TryAdd(tenantId, region);
        return Task.FromResult(new RegionRegistration(Regions[tenantId], added));
    }
}

internal sealed class InMemoryOnboardingRepository : IOnboardingRepository
{
    private readonly TimeProvider _clock;

    public InMemoryOnboardingRepository(TimeProvider clock)
    {
        _clock = clock;
    }

    public List<Tenant> Tenants { get; } = [];

    public List<AppUser> Users { get; } = [];

    public List<PendingConsentRequest> Requests { get; } = [];

    public List<AuditLog> Audits { get; } = [];

    public int SaveCount { get; private set; }

    public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Tenants.Find(t => t.TenantId == tenantId));

    public Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        Tenants.Add(tenant);
        return Task.CompletedTask;
    }

    public Task<AppUser?> FindAppUserAsync(Guid tenantId, Guid entraObjectId, CancellationToken cancellationToken)
        => Task.FromResult(Users.Find(u => u.TenantId == tenantId && u.EntraObjectId == entraObjectId));

    public Task AddAppUserAsync(AppUser user, CancellationToken cancellationToken)
    {
        Users.Add(user);
        return Task.CompletedTask;
    }

    public Task<PendingConsentRequest?> FindOpenConsentRequestAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Requests.LastOrDefault(r => r.TenantId == tenantId && r.IsUsable(_clock.GetUtcNow())));

    public Task<PendingConsentRequest?> FindOpenConsentRequestAsync(
        Guid tenantId,
        Guid requestedByObjectId,
        string sentToEmail,
        CancellationToken cancellationToken)
        => Task.FromResult(Requests.LastOrDefault(r =>
            r.TenantId == tenantId
            && r.RequestedByObjectId == requestedByObjectId
            && r.IsAddressedTo(sentToEmail)
            && r.IsUsable(_clock.GetUtcNow())));

    public Task<IReadOnlyList<PendingConsentRequest>> ListConsentRequestsSentSinceAsync(
        Guid tenantId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PendingConsentRequest>>(
            [.. Requests.Where(r => r.TenantId == tenantId && r.LastSentUtc >= sinceUtc)]);

    public Task<PendingConsentRequest?> FindConsentRequestByTokenAsync(string token, CancellationToken cancellationToken)
        => Task.FromResult(Requests.Find(r => r.Token == token && r.IsUsable(_clock.GetUtcNow())));

    public Task AddConsentRequestAsync(PendingConsentRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.CompletedTask;
    }

    public Task AddAuditAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        Audits.Add(entry);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;

        // Mimic the identity column, so an attempt row can be resolved once it is "saved".
        foreach (var audit in Audits.Where(a => a.AuditLogId == 0))
        {
            AuditLogIdProperty.SetValue(audit, ++_lastAuditLogId);
        }

        return Task.CompletedTask;
    }

    private static readonly System.Reflection.PropertyInfo AuditLogIdProperty =
        typeof(AuditLog).GetProperty(nameof(AuditLog.AuditLogId))!;

    private long _lastAuditLogId;
}

/// <summary>Everything an <see cref="OnboardingService"/> test needs, wired together.</summary>
internal sealed class OnboardingHarness
{
    public const string Region = "eu";

    public static readonly DateTimeOffset Start = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    public OnboardingHarness()
    {
        Clock = new FakeTimeProvider(Start);
        Repository = new InMemoryOnboardingRepository(Clock);
        Service = new OnboardingService(
            Repository,
            Email,
            Verifier,
            Jobs,
            Regions,
            DirectoryInfo,
            Clock,
            NullLogger<OnboardingService>.Instance);
    }

    public FakeTimeProvider Clock { get; }

    public InMemoryOnboardingRepository Repository { get; }

    public FakeEmailSender Email { get; } = new();

    public FakeConsentVerifier Verifier { get; } = new();

    public RecordingJobEnqueuer Jobs { get; } = new();

    public FakeRegionDirectory Regions { get; } = new();

    public FakeDirectoryInfo DirectoryInfo { get; } = new();

    public OnboardingService Service { get; }

    public Tenant Tenant(Guid tenantId) => Repository.Tenants.Single(t => t.TenantId == tenantId);

    public AppUser User(Guid objectId) => Repository.Users.Single(u => u.EntraObjectId == objectId);
}
