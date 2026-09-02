using System.Globalization;

namespace Mlcp.Shared.Resilience;

/// <summary>
/// Reads the retry hints Microsoft returns when it throttles us.
/// </summary>
/// <remarks>
/// Two headers matter and they are not interchangeable. <c>Retry-After</c> is the standard one
/// used by Graph, Resource Manager and Cost Management. The Consumption APIs additionally
/// return <c>x-ms-ratelimit-microsoft.consumption-retry-after</c>, and ignoring it is how a
/// client ends up hammering an endpoint that has already told it to stop
/// (docs/02-api-reference.md §2.4).
/// </remarks>
public static class RetryAfterReader
{
    public const string RetryAfterHeader = "Retry-After";

    public const string ConsumptionRetryAfterHeader = "x-ms-ratelimit-microsoft.consumption-retry-after";

    /// <summary>
    /// Upper bound on an honoured delay. Microsoft occasionally returns very long hints; a job
    /// that would sleep past its next scheduled run should fail and be re-queued instead of
    /// holding a worker.
    /// </summary>
    public static TimeSpan MaximumHonouredDelay { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Returns the longest delay any recognised header asks for, or null when none is present
    /// or parseable. The longest is chosen because both headers can appear and each represents
    /// a separate limit that must be satisfied.
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
            if (longest is null || candidate > longest)
            {
                longest = candidate;
            }
        }

        return longest is { } delay ? Clamp(delay) : null;
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
                var until = date - timeProvider.GetUtcNow();

                if (until > TimeSpan.Zero)
                {
                    yield return until;
                }
            }
        }

        foreach (var header in new[] { ConsumptionRetryAfterHeader, RetryAfterHeader })
        {
            if (!TryGetHeaderValues(response, header, out var values))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    yield return TimeSpan.FromSeconds(seconds);
                }
            }
        }
    }

    private static bool TryGetHeaderValues(HttpResponseMessage response, string name, out IEnumerable<string> values)
    {
        if (response.Headers.TryGetValues(name, out var fromHeaders))
        {
            values = fromHeaders;
            return true;
        }

        if (response.Content?.Headers.TryGetValues(name, out var fromContent) == true)
        {
            values = fromContent;
            return true;
        }

        values = [];
        return false;
    }

    private static TimeSpan Clamp(TimeSpan delay)
        => delay > MaximumHonouredDelay ? MaximumHonouredDelay : delay;
}
