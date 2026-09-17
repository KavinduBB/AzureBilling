using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Mlcp.Web.Infrastructure;

/// <summary>
/// Request rate limits for the onboarding endpoints that cause outbound effects.
/// </summary>
/// <remarks>
/// <para>
/// These sit in front of the durable limits: <see cref="Mlcp.Application.Onboarding.ConsentRequestPolicy"/>
/// counts emails in the database, and Service Bus de-duplicates re-probe requests. The
/// middleware limits are per instance; their job is to shed bursts cheaply before any of that
/// work starts, not to be the only control.
/// </para>
/// <para>
/// Partitions are keyed by the validated <c>tid</c>/<c>oid</c>, falling back to the client
/// address for anything unauthenticated.
/// </para>
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary>"Ask my admin": a handful of attempts per person per minute.</summary>
    public const string ConsentRequest = "consent-request";

    /// <summary>Starting admin consent: each start issues a nonce and writes an audit row.</summary>
    public const string Connect = "connect";

    /// <summary>The Owner's "Check again" button: once per five minutes per tenant (ADR-016 rule 5).</summary>
    public const string CheckAgain = "check-again";

    public static IServiceCollection AddMlcpRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(ConsentRequest, context => RateLimitPartition.GetFixedWindowLimiter(
                PerUser(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            options.AddPolicy(Connect, context => RateLimitPartition.GetFixedWindowLimiter(
                PerUser(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            options.AddPolicy(CheckAgain, context => RateLimitPartition.GetFixedWindowLimiter(
                PerTenant(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
        });
    }

    private static string PerUser(HttpContext context)
        => context.User.TryGetSignedInUser(out var user)
            ? $"user:{user.TenantId:N}:{user.ObjectId:N}"
            : $"ip:{context.Connection.RemoteIpAddress}";

    private static string PerTenant(HttpContext context)
        => context.User.TryGetSignedInUser(out var user)
            ? $"tenant:{user.TenantId:N}"
            : $"ip:{context.Connection.RemoteIpAddress}";
}
