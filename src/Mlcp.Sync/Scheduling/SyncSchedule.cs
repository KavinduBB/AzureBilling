using System.Globalization;
using System.Text.RegularExpressions;
using Mlcp.Domain.Sync;

namespace Mlcp.Sync.Scheduling;

/// <summary>
/// Decides when a tenant's job is due.
/// </summary>
/// <remarks>
/// Every tenant is offset by a stable per-tenant amount rather than all running on the hour.
/// Without it, every customer's Graph and Cost Management calls would land in the same minute,
/// which is the fastest way to hit a per-application throttle (docs/02-api-reference.md §1.3).
/// The offset is applied through the message's scheduled enqueue time, not just its id.
/// </remarks>
public static class SyncSchedule
{
    /// <summary>Width of the stagger window, in minutes.</summary>
    public const int StaggerWindowMinutes = 60;

    /// <summary>A discovery that has been queued is not picked again for this long after its slot.</summary>
    public static TimeSpan DiscoveryLease { get; } = TimeSpan.FromHours(1);

    /// <summary>The stable offset, in minutes, for a tenant.</summary>
    public static int StaggerOffsetMinutes(Guid tenantId)
    {
        // A stable hash (FNV-1a) of the tenant id, not GetHashCode, which is randomised per process.
        Span<byte> bytes = stackalloc byte[16];
        tenantId.TryWriteBytes(bytes);

        uint hash = 2166136261;

        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 16777619;
        }

        return (int)(hash % StaggerWindowMinutes);
    }

    /// <summary>
    /// The tenant's next staggered slot at or after <paramref name="nowUtc"/>: its offset minute in
    /// the current hour, or in the next hour if that minute has passed.
    /// </summary>
    public static DateTimeOffset NextStaggeredSlot(Guid tenantId, DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        var slot = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero)
            .AddMinutes(StaggerOffsetMinutes(tenantId));

        return slot < utc ? slot.AddHours(1) : slot;
    }

    /// <summary>Deduplication key for a scheduled slot: the same slot is the same unit of work.</summary>
    public static string SlotKey(DateTimeOffset slotUtc)
        => string.Create(CultureInfo.InvariantCulture, $"slot:{slotUtc.ToUniversalTime():yyyyMMddHHmm}");

    /// <summary>Cadence for each job, from docs/03-architecture.md §6.1.</summary>
    public static TimeSpan Interval(SyncJobType jobType) => jobType switch
    {
        SyncJobType.CapabilityDiscovery => TimeSpan.FromDays(7),
        SyncJobType.LicenseSkuSync => TimeSpan.FromHours(4),
        SyncJobType.UserAssignmentSync => TimeSpan.FromDays(1),
        SyncJobType.UsageReportSync => TimeSpan.FromDays(1),
        SyncJobType.BillingSubscriptionSync => TimeSpan.FromHours(6),
        SyncJobType.TransactionSyncUnbilled => TimeSpan.FromDays(1),
        SyncJobType.TransactionSyncBilled => TimeSpan.FromDays(1),
        SyncJobType.InvoiceSync => TimeSpan.FromDays(1),
        SyncJobType.AzureCostSummarySync => TimeSpan.FromHours(6),
        SyncJobType.AzureCostDetailSync => TimeSpan.FromDays(1),
        SyncJobType.ReservationSync => TimeSpan.FromDays(1),
        SyncJobType.AdvisorSync => TimeSpan.FromDays(1),
        SyncJobType.SnapshotJob => TimeSpan.FromDays(1),
        SyncJobType.AssociatedTenantDiscovery => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(1),
    };

    /// <summary>Backoff before failure retry <paramref name="failedAttempt"/> + 1: 2, 4, 8, 16 minutes, capped at 1 hour.</summary>
    public static TimeSpan RetryDelay(int failedAttempt)
    {
        var exponent = Math.Clamp(failedAttempt - 1, 0, 10);
        var delay = TimeSpan.FromMinutes(2 * Math.Pow(2, exponent));
        return delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay;
    }
}

/// <summary>
/// The consent-verification attempt convention (ADR-018): the deduplication key ends with
/// <c>:attempt:{n}</c>; a key without the suffix is attempt 1. The web host enqueues attempt 1 at
/// +2 minutes; the worker re-enqueues attempt 2 at +5 minutes and attempt 3 at +15 minutes.
/// </summary>
public static partial class ConsentVerificationAttempts
{
    public const int MaxAttempts = 3;

    public static (string Root, int Attempt) Parse(string deduplicationKey)
    {
        ArgumentNullException.ThrowIfNull(deduplicationKey);

        var match = AttemptSuffix().Match(deduplicationKey);

        return match.Success
            ? (deduplicationKey[..match.Index], int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            : (deduplicationKey, 1);
    }

    public static string KeyFor(string root, int attempt)
        => string.Create(CultureInfo.InvariantCulture, $"{root}:attempt:{attempt}");

    /// <summary>Delay before attempt <paramref name="nextAttempt"/>: +5 minutes for attempt 2, +15 minutes for attempt 3.</summary>
    public static TimeSpan DelayBefore(int nextAttempt)
        => nextAttempt <= 2 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);

    [GeneratedRegex(@":attempt:(\d{1,4})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex AttemptSuffix();
}
