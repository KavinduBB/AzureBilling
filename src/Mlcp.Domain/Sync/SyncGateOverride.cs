using Mlcp.Domain.Common;

namespace Mlcp.Domain.Sync;

/// <summary>
/// A one-shot operator approval that lets the next full load of one job for one tenant pass the
/// volume check (ADR-024 §4).
/// </summary>
/// <remarks>
/// <para>
/// A genuine large deletion (a tenant cancelling most of its subscriptions, say) looks exactly
/// like a truncated response, so without this the gate would block that job forever. The
/// override is the escape hatch, and it is deliberately narrow: one tenant, one job, one run,
/// at most 24 hours. It waives only the volume rule; field violations still block.
/// </para>
/// <para>
/// Recorded by platform operations, never by a customer. The consuming run writes the
/// override id and approver into its <see cref="SyncRun.ValidationNotes"/> and logs an audit
/// event, and becomes the new baseline. Consumption is a conditional update inside the merge
/// transaction, so two runs cannot both use one override and a run whose merge rolls back does
/// not burn it.
/// </para>
/// </remarks>
public class SyncGateOverride : TenantEntity
{
    /// <summary>ADR-024 §4: overrides expire after 24 hours.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);

    public Guid SyncGateOverrideId { get; private set; }

    public SyncJobType JobType { get; private set; }

    public DateTimeOffset ExpiresUtc { get; private set; }

    /// <summary>Why the drop is genuine, in the operator's words. Copied into the run's notes.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>The operator who approved it (UPN or ops identity).</summary>
    public string ApprovedBy { get; private set; } = string.Empty;

    /// <summary>The run that used the override, or null while it is still available.</summary>
    public Guid? ConsumedBySyncRunId { get; private set; }

    public DateTimeOffset? ConsumedUtc { get; private set; }

    private SyncGateOverride()
    {
    }

    private SyncGateOverride(Guid tenantId, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
    }

    /// <param name="tenantId">The tenant whose job is blocked.</param>
    /// <param name="jobType">The blocked job.</param>
    /// <param name="reason">Why the drop is genuine.</param>
    /// <param name="approvedBy">The approving operator.</param>
    /// <param name="nowUtc">Approval time.</param>
    /// <param name="lifetime">How long the approval stands; at most <see cref="MaximumLifetime"/>.</param>
    public static SyncGateOverride Approve(
        Guid tenantId,
        SyncJobType jobType,
        string reason,
        string approvedBy,
        DateTimeOffset nowUtc,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);

        if (jobType == SyncJobType.Unknown)
        {
            throw new ArgumentException("An override must name a concrete job type.", nameof(jobType));
        }

        var duration = lifetime ?? MaximumLifetime;

        if (duration <= TimeSpan.Zero || duration > MaximumLifetime)
        {
            throw new DomainException(
                $"An override lasts between zero and {MaximumLifetime.TotalHours:0} hours; {duration} was requested.");
        }

        return new SyncGateOverride(tenantId, nowUtc)
        {
            SyncGateOverrideId = Guid.NewGuid(),
            JobType = jobType,
            ExpiresUtc = nowUtc + duration,
            Reason = reason.Trim(),
            ApprovedBy = approvedBy.Trim(),
        };
    }

    /// <summary>True when a run starting at <paramref name="nowUtc"/> may use this override.</summary>
    public bool IsAvailableAt(DateTimeOffset nowUtc) => ConsumedBySyncRunId is null && nowUtc < ExpiresUtc;

    /// <summary>The grant the gate reads.</summary>
    public SyncGateOverrideGrant ToGrant() => new(SyncGateOverrideId, ApprovedBy, Reason);

    /// <summary>
    /// Marks the override used. The store performs the same change as a conditional update so
    /// that concurrent consumers cannot both succeed; this method states the rule.
    /// </summary>
    public void Consume(Guid syncRunId, DateTimeOffset nowUtc)
    {
        if (ConsumedBySyncRunId is { } consumer)
        {
            throw new DomainException($"Override {SyncGateOverrideId} was already consumed by run {consumer}.");
        }

        if (nowUtc >= ExpiresUtc)
        {
            throw new DomainException($"Override {SyncGateOverrideId} expired at {ExpiresUtc:O}.");
        }

        ConsumedBySyncRunId = syncRunId;
        ConsumedUtc = nowUtc;
        Touch(nowUtc);
    }
}
