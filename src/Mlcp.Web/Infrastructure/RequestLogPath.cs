using Microsoft.AspNetCore.Routing;

namespace Mlcp.Web.Infrastructure;

/// <summary>
/// What request logging records as the path.
/// </summary>
/// <remarks>
/// The matched route template (<c>onboarding/consent/{token}</c>) rather than the raw path
/// (<c>/onboarding/consent/Zk3…</c>), so values that are secrets in the URL never reach a log
/// sink. Requests that matched no endpoint (static files, 404s) carry nothing sensitive and log
/// their path without the query string.
/// </remarks>
public static class RequestLogPath
{
    public static string For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } template })
        {
            return "/" + template.TrimStart('/');
        }

        return context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
    }
}
