using System.Globalization;
using Mlcp.Domain.Sync;

namespace Mlcp.Sync.Scheduling;

/// <summary>A unit of work placed on the queue: one job, for one tenant, once.</summary>
/// <param name="TenantId">The tenant to sync.</param>
/// <param name="JobType">Which job to run.</param>
/// <param name="ScheduledForUtc">The slot this message was scheduled into.</param>
/// <param name="CorrelationId">Ties the queue message, the run record and the logs together.</param>
public sealed record SyncJobMessage(
    Guid TenantId,
    SyncJobType JobType,
    DateTimeOffset ScheduledForUtc,
    string CorrelationId)
{
    /// <summary>
    /// Service Bus session id. Sessions are per tenant, which is what gives ordering within a
    /// tenant while still letting different tenants process in parallel
    /// (docs/03-architecture.md §6.1).
    /// </summary>
    public string SessionId => TenantId.ToString("D");

    /// <summary>
    /// Duplicate-detection id. One job, for one tenant, in one scheduling slot is the same unit
    /// of work however many times the scheduler enqueues it, so a scheduler that restarts or
    /// runs twice does not cause a second sync.
    /// </summary>
    public string MessageId => string.Create(
        CultureInfo.InvariantCulture,
        $"{TenantId:D}:{JobType}:{ScheduledForUtc:yyyyMMddHHmm}");
}

/// <summary>Places jobs on the queue.</summary>
public interface ISyncJobDispatcher
{
    Task DispatchAsync(SyncJobMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Decides when a tenant's job is due.
/// </summary>
/// <remarks>
/// Every tenant is offset by a stable per-tenant amount rather than all running on the hour.
/// Without it, every customer's Graph and Cost Management calls would land in the same minute,
/// which is the fastest way to hit a per-application throttle and take every tenant down at
/// once (docs/02-api-reference.md §1.3).
/// </remarks>
public static class SyncSchedule
{
    /// <summary>Width of the stagger window, in minutes.</summary>
    public const int StaggerWindowMinutes = 60;

    /// <summary>The stable offset, in minutes, for a tenant.</summary>
    public static int StaggerOffsetMinutes(Guid tenantId)
    {
        // A stable hash of the tenant id, not GetHashCode: that is randomised per process, so
        // the offset would change on every restart and the stagger would not hold.
        Span<byte> bytes = stackalloc byte[16];
        tenantId.TryWriteBytes(bytes);

        uint hash = 2166136261;

        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 16777619;
        }

        return (int)(hash % StaggerWindowMinutes);
    }

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
}
