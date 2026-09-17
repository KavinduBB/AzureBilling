using Mlcp.Domain.Sync;

namespace Mlcp.Application.Sync;

/// <summary>
/// Places a job for one tenant on the work queue. Request handlers use this instead of calling
/// Microsoft themselves (CLAUDE.md rule 5); the worker consumes it.
/// </summary>
/// <remarks>
/// Implemented over Azure Service Bus with per-tenant sessions and duplicate detection
/// (Mlcp.Integration.Azure). <paramref name="notBeforeUtc"/> maps to a scheduled enqueue time,
/// so delayed work (consent propagation, re-consent probes, throttling back-off) never sleeps
/// inside a request or a message handler.
/// </remarks>
public interface ISyncJobEnqueuer
{
    /// <param name="tenantId">The tenant the job belongs to; also the session id.</param>
    /// <param name="jobType">The job to run.</param>
    /// <param name="deduplicationKey">
    /// Stable key for the unit of work (for example a scheduling slot or a consent nonce).
    /// Two enqueues with the same key inside the duplicate-detection window run once.
    /// </param>
    /// <param name="notBeforeUtc">Earliest time the job may run; null for immediately.</param>
    /// <param name="correlationId">Ties the request, the queue message and the run together.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task EnqueueAsync(
        Guid tenantId,
        SyncJobType jobType,
        string deduplicationKey,
        DateTimeOffset? notBeforeUtc,
        string correlationId,
        CancellationToken cancellationToken);
}
