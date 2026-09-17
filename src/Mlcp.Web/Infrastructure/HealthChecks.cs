using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Mlcp.Web.Infrastructure;

/// <summary>Tags that split liveness from readiness (ADR-026).</summary>
public static class HealthCheckTags
{
    /// <summary>Checks a dependency the app cannot serve without. Used by <c>/health/ready</c> only.</summary>
    public const string Ready = "ready";
}

/// <summary>
/// Pings Redis. The MSAL token cache and the consent nonces live there, so an instance that
/// cannot reach it cannot sign anyone in and must not take traffic.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection;

    public RedisHealthCheck(Lazy<Task<IConnectionMultiplexer>> connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var multiplexer = await _connection.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            var latency = await multiplexer.GetDatabase().PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy($"Redis answered in {latency.TotalMilliseconds:F0} ms.");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or OperationCanceledException)
        {
            // The exception message can include the endpoint; the health response is anonymous,
            // so only the type goes out.
            return HealthCheckResult.Unhealthy($"Redis is unreachable ({ex.GetType().Name}).");
        }
    }
}
