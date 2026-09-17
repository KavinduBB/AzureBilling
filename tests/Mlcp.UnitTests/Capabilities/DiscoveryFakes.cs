using Mlcp.Application.Onboarding;
using Mlcp.Application.Sync;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.Integration.Azure.Messaging;
using Mlcp.Shared.Resilience;

namespace Mlcp.UnitTests.Capabilities;

/// <summary>An in-memory <see cref="ITenantOnboardingStore"/> holding one tenant.</summary>
public sealed class InMemoryOnboardingStore : ITenantOnboardingStore
{
    private readonly Dictionary<OnboardingStepName, OnboardingStep> _steps = [];

    public InMemoryOnboardingStore(Tenant? tenant)
    {
        Tenant = tenant;
    }

    public Tenant? Tenant { get; }

    public TenantCapabilityProfile? Profile { get; set; }

    public int SaveCount { get; private set; }

    public IReadOnlyDictionary<OnboardingStepName, OnboardingStep> Steps => _steps;

    public List<Mlcp.Domain.Audit.AuditLog> Audits { get; } = [];

    public Task AddAuditAsync(Mlcp.Domain.Audit.AuditLog entry, CancellationToken cancellationToken)
    {
        Audits.Add(entry);
        return Task.CompletedTask;
    }

    public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Tenant?.TenantId == tenantId ? Tenant : null);

    public Task<TenantCapabilityProfile?> FindCapabilityProfileAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Profile?.TenantId == tenantId ? Profile : null);

    public Task AddCapabilityProfileAsync(TenantCapabilityProfile profile, CancellationToken cancellationToken)
    {
        Profile = profile;
        return Task.CompletedTask;
    }

    public Task<OnboardingStep> GetOrCreateStepAsync(Guid tenantId, OnboardingStepName step, CancellationToken cancellationToken)
    {
        if (!_steps.TryGetValue(step, out var existing))
        {
            existing = OnboardingStep.Pending(tenantId, step, DateTimeOffset.UnixEpoch);
            _steps[step] = existing;
        }

        return Task.FromResult(existing);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakeGraphProbe : IGraphCapabilityProbe
{
    public Func<GraphProbeResult> Result { get; set; } = () => GraphProbeResult.NotReachable;

    public Func<ProbeCallOutcome> Floor { get; set; } = () => ProbeCallOutcome.Success;

    public int Calls { get; private set; }

    public Task<GraphProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result());
    }

    public Task<ProbeCallOutcome> ProbeFloorAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Floor());
    }
}

public sealed class FakeArmProbe : IAzureCapabilityProbe
{
    public Func<ArmProbeResult> Result { get; set; } = () => ArmProbeResult.NotReachable;

    public int Calls { get; private set; }

    public Task<ArmProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result());
    }
}

public sealed class FakeBillingProbe : IBillingCapabilityProbe
{
    public Func<BillingProbeResult> Result { get; set; } = () => BillingProbeResult.NotReachable;

    public int Calls { get; private set; }

    public Task<BillingProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result());
    }
}

public sealed class FakeConsentVerifier : IConsentVerifier
{
    public ConsentVerificationResult Result { get; set; } = new(ConsentVerificationStatus.Verified);

    public Task<ConsentVerificationResult> VerifyAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Result);
}

public sealed class RecordingEnqueuer : ISyncJobEnqueuer, ISyncJobRequeuer
{
    public List<SyncJobMessage> Enqueued { get; } = [];

    public List<SyncJobMessage> Requeued { get; } = [];

    public Task EnqueueAsync(
        Guid tenantId,
        SyncJobType jobType,
        string deduplicationKey,
        DateTimeOffset? notBeforeUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        Enqueued.Add(new SyncJobMessage(tenantId, jobType, deduplicationKey, correlationId) { NotBeforeUtc = notBeforeUtc });
        return Task.CompletedTask;
    }

    public Task RequeueAsync(SyncJobMessage message, CancellationToken cancellationToken)
    {
        Requeued.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Probe fixtures shared by the discovery and processor tests.</summary>
public static class ProbeFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    public static Tenant ProvisioningTenant(Guid? tenantId = null)
    {
        var tenant = Tenant.Register(tenantId ?? Guid.NewGuid(), "Contoso", "contoso.example", "westeurope", Now.AddDays(-1));
        tenant.AwaitConsentVerification(Guid.NewGuid(), Now.AddDays(-1));
        tenant.ConfirmConsent(Guid.NewGuid(), Now.AddDays(-1));
        return tenant;
    }

    public static Tenant ActiveTenant(Guid? tenantId = null)
    {
        var tenant = ProvisioningTenant(tenantId);
        tenant.Activate(Now.AddDays(-1));
        return tenant;
    }

    public static GraphProbeResult FloorOnly(bool usage = false, Guid? owner = null) => new(
        SubscribedSkusReadable: true,
        DirectorySubscriptionsReadable: true,
        UsageReportsReadable: usage,
        OwnerTenantId: owner,
        OrganizationDisplayName: "Contoso",
        DefaultDomain: "contoso.example",
        VerifiedDomains: ["contoso.example", "Contoso.onmicrosoft.com"],
        SubscribedSkus: ProbeCallOutcome.Success,
        DirectorySubscriptions: ProbeCallOutcome.Success,
        Organization: ProbeCallOutcome.Success,
        UsageReports: usage ? ProbeCallOutcome.Success : ProbeCallOutcome.Denied);

    public static ProbeCallOutcome Transient503 { get; } = new(MicrosoftFailureKind.Transient, 503);

    public static ArmProbeResult McaAzure(bool costReadable) => new(
        [new AzureSubscriptionProbe(Guid.Parse("5b1c0000-0000-0000-0000-000000000001"), "Production", AgreementType.Mca, true)],
        CostManagementQueryable: costReadable,
        SubscriptionList: ProbeCallOutcome.Success,
        SubscriptionCostQuery: costReadable ? ProbeCallOutcome.Success : ProbeCallOutcome.Denied,
        RootManagementGroupCostQuery: ProbeCallOutcome.NotAttempted);
}
