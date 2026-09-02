using Mlcp.Domain.Common;

namespace Mlcp.Domain.Tenancy;

/// <summary>
/// One step of the onboarding state machine for one tenant. Transitions are idempotent so a
/// resumed or replayed job reaches the same state (docs/03-architecture.md §3).
/// </summary>
public class OnboardingStep : TenantEntity
{
    public Guid OnboardingStepId { get; private set; }

    public OnboardingStepName Step { get; private set; }

    public OnboardingStepStatus Status { get; private set; }

    public DateTimeOffset? StartedUtc { get; private set; }

    public DateTimeOffset? CompletedUtc { get; private set; }

    public string? Error { get; private set; }

    private OnboardingStep()
    {
    }

    private OnboardingStep(Guid tenantId, OnboardingStepName step, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
        OnboardingStepId = Guid.NewGuid();
        Step = step;
        Status = OnboardingStepStatus.Pending;
    }

    public static OnboardingStep Pending(Guid tenantId, OnboardingStepName step, DateTimeOffset nowUtc)
        => new(tenantId, step, nowUtc);

    public void Begin(DateTimeOffset nowUtc)
    {
        if (Status == OnboardingStepStatus.Completed)
        {
            return;
        }

        Status = OnboardingStepStatus.InProgress;
        StartedUtc = nowUtc;
        Error = null;
        Touch(nowUtc);
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        Status = OnboardingStepStatus.Completed;
        CompletedUtc = nowUtc;
        Error = null;
        Touch(nowUtc);
    }

    public void Fail(string error, DateTimeOffset nowUtc)
    {
        Status = OnboardingStepStatus.Failed;
        Error = error;
        Touch(nowUtc);
    }

    public void Skip(string reason, DateTimeOffset nowUtc)
    {
        Status = OnboardingStepStatus.Skipped;
        Error = reason;
        CompletedUtc = nowUtc;
        Touch(nowUtc);
    }
}
