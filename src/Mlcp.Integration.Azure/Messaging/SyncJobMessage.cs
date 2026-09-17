using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mlcp.Domain.Sync;

namespace Mlcp.Integration.Azure.Messaging;

/// <summary>A unit of work on the sync queue: one job, for one tenant, once per deduplication key.</summary>
/// <param name="TenantId">The tenant; also the Service Bus session id.</param>
/// <param name="JobType">Which job to run.</param>
/// <param name="DeduplicationKey">
/// Stable key for the unit of work (a scheduling slot, a consent nonce plus attempt). Part of the
/// message id, so Service Bus duplicate detection drops a second enqueue of the same work.
/// </param>
/// <param name="CorrelationId">Ties the request, the message, the run and the logs together.</param>
public sealed record SyncJobMessage(
    Guid TenantId,
    SyncJobType JobType,
    string DeduplicationKey,
    string CorrelationId)
{
    /// <summary>Service Bus caps message ids at 128 characters.</summary>
    public const int MaxMessageIdLength = 128;

    /// <summary>Earliest time the job may run; mapped to <c>ScheduledEnqueueTime</c>.</summary>
    public DateTimeOffset? NotBeforeUtc { get; init; }

    /// <summary>1-based delivery attempt for failure retries (the consumer re-enqueues with backoff).</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>How many times the job has been re-queued because Microsoft throttled it.</summary>
    public int ThrottleCount { get; init; }

    /// <summary>Per-tenant session, so a tenant's jobs run in order while tenants run in parallel.</summary>
    public string SessionId => TenantId.ToString("D");

    /// <summary>
    /// <c>{tenant}:{job}:{deduplicationKey}</c>. A key that would exceed 128 characters is replaced
    /// by its SHA-256 prefix so the id stays stable and within the limit.
    /// </summary>
    public string MessageId
    {
        get
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"{TenantId:D}:{JobType}:{DeduplicationKey}");

            if (id.Length <= MaxMessageIdLength)
            {
                return id;
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeduplicationKey)))[..40];
            return string.Create(CultureInfo.InvariantCulture, $"{TenantId:D}:{JobType}:{hash}");
        }
    }

    /// <summary>A copy for a failure retry: next attempt, distinct message id, scheduled later.</summary>
    public SyncJobMessage ForRetry(DateTimeOffset notBeforeUtc)
        => this with
        {
            DeduplicationKey = WithSuffix(DeduplicationKey, "retry", Attempt + 1),
            Attempt = Attempt + 1,
            NotBeforeUtc = notBeforeUtc,
        };

    /// <summary>A copy for a throttling re-queue: same attempt, distinct message id, scheduled later.</summary>
    public SyncJobMessage ForThrottle(DateTimeOffset notBeforeUtc)
        => this with
        {
            DeduplicationKey = WithSuffix(DeduplicationKey, "throttle", ThrottleCount + 1),
            ThrottleCount = ThrottleCount + 1,
            NotBeforeUtc = notBeforeUtc,
        };

    private static string WithSuffix(string key, string label, int number)
    {
        var marker = ":" + label + ":";
        var index = key.LastIndexOf(marker, StringComparison.Ordinal);
        var root = index >= 0 ? key[..index] : key;
        return string.Create(CultureInfo.InvariantCulture, $"{root}{marker}{number}");
    }
}
