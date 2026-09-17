using System.Globalization;

namespace Mlcp.Shared.Resilience;

/// <summary>
/// Reads the retry hints Microsoft returns when it throttles us (ADR-016 rule 8).
/// </summary>
/// <remarks>
/// <para>
/// Three headers matter and they are not interchangeable. <c>Retry-After</c> is the standard one
/// (delta-seconds or HTTP-date). The Consumption APIs add
/// <c>x-ms-ratelimit-microsoft.consumption-retry-after</c> (docs/02 §2.4), and the Cost
/// Management Query API adds <c>x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after</c> when
/// the tenant's QPU quota is exhausted
/// (<see href="https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/manage-automation">Cost Management automation</see>).
/// </para>
/// <para>
/// A hint of zero, a negative number or a date in the past carries no information, so it reads
/// as "no hint" and the caller's own exponential backoff applies. Retrying immediately on
/// <c>Retry-After: 0</c> is how a client turns one 429 into a burst of them.
/// </para>
/// <para>
/// No upper bound is applied here: whether a long hint is honoured or turned into a re-queue is a
/// budget decision made by the pipeline (ADR-016 rule 7), not a parsing one.
/// </para>
/// </remarks>
public static class RetryAfterReader
{
    public const string RetryAfterHeader = "Retry-After";

    public const string ConsumptionRetryAfterHeader = "x-ms-ratelimit-microsoft.consumption-retry-after";

    public const string CostManagementQpuRetryAfterHeader = "x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after";

    private static readonly string[] SecondsHeaders = [ConsumptionRetryAfterHeader, CostManagementQpuRetryAfterHeader];

    /// <summary>
    /// Returns the longest positive delay any recognised header asks for, or null when none is
    /// present, parseable and positive. The longest wins because each header reports a separate
    /// limit that must be satisfied.
    /// </summary>
    public static TimeSpan? Read(HttpResponseMessage? response, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (response is null)
        {
            return null;
        }

        TimeSpan? longest = null;

        foreach (var candidate in ReadAll(response, timeProvider))
        {
            if (candidate > TimeSpan.Zero && (longest is null || candidate > longest))
            {
                longest = candidate;
            }
        }

        return longest;
    }

    private static IEnumerable<TimeSpan> ReadAll(HttpResponseMessage response, TimeProvider timeProvider)
    {
        // The typed header handles both the delta-seconds and the HTTP-date forms.
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                yield return delta;
            }
            else if (retryAfter.Date is { } date)
            {
                yield return date - timeProvider.GetUtcNow();
            }
        }

        foreach (var header in SecondsHeaders)
        {
            foreach (var value in GetHeaderValues(response, header))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && seconds > 0
                    && seconds < TimeSpan.MaxValue.TotalSeconds)
                {
                    yield return TimeSpan.FromSeconds(seconds);
                }
            }
        }
    }

    private static IEnumerable<string> GetHeaderValues(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var fromHeaders))
        {
            return fromHeaders;
        }

        if (response.Content?.Headers.TryGetValues(name, out var fromContent) == true)
        {
            return fromContent;
        }

        return [];
    }
}
