using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Web.Infrastructure;

/// <summary>
/// Isolation layer 4: rejects any request whose payload names a tenant other than the caller's.
/// </summary>
/// <remarks>
/// <para>
/// A tenant id appearing in a route, query string or request body is never used to select data
/// — <see cref="ITenantContext"/> is the only source for that. But an endpoint that accepts one
/// is still a probe: differing responses for "exists elsewhere" and "does not exist" would let
/// a caller enumerate other customers.
/// </para>
/// <para>
/// The response is therefore <strong>404, not 403</strong>. 403 confirms the resource exists
/// and is merely off-limits; 404 makes another tenant's data indistinguishable from data that
/// was never there (docs/03-architecture.md §5.1).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class TenantScopedAttribute : ActionFilterAttribute
{
    /// <summary>Parameter and property names inspected for a tenant id.</summary>
    private static readonly string[] TenantIdNames = ["tenantId", "tenantid", "TenantId"];

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tenantContext = context.HttpContext.RequestServices.GetService(typeof(ITenantContext)) as ITenantContext;
        var currentTenantId = tenantContext?.TenantId;

        foreach (var claimed in EnumerateClaimedTenantIds(context))
        {
            if (currentTenantId is null || claimed != currentTenantId)
            {
                var logger = context.HttpContext.RequestServices
                    .GetService(typeof(ILoggerFactory)) as ILoggerFactory;

                logger?.CreateLogger<TenantScopedAttribute>().LogWarning(
                    "Request named tenant {ClaimedTenantId} while tenant {CurrentTenantId} is in scope. Returning 404.",
                    claimed,
                    currentTenantId);

                context.Result = new NotFoundResult();
                return;
            }
        }

        base.OnActionExecuting(context);
    }

    /// <summary>
    /// Yields every tenant id the request asserts, from route values and from bound action
    /// arguments including a <c>TenantId</c> property on a bound model.
    /// </summary>
    private static IEnumerable<Guid> EnumerateClaimedTenantIds(ActionExecutingContext context)
    {
        foreach (var name in TenantIdNames)
        {
            if (context.RouteData.Values.TryGetValue(name, out var routeValue)
                && TryParse(routeValue, out var fromRoute))
            {
                yield return fromRoute;
            }
        }

        foreach (var (key, value) in context.ActionArguments)
        {
            if (value is null)
            {
                continue;
            }

            if (TenantIdNames.Contains(key, StringComparer.OrdinalIgnoreCase) && TryParse(value, out var fromArgument))
            {
                yield return fromArgument;
                continue;
            }

            // A bound DTO that carries a TenantId is treated the same as a route value.
            var property = value.GetType().GetProperty("TenantId");

            if (property is not null && TryParse(property.GetValue(value), out var fromModel))
            {
                yield return fromModel;
            }
        }
    }

    private static bool TryParse(object? value, out Guid tenantId)
    {
        switch (value)
        {
            case Guid guid when guid != Guid.Empty:
                tenantId = guid;
                return true;

            case string text when Guid.TryParse(text, CultureInfo.InvariantCulture, out var parsed) && parsed != Guid.Empty:
                tenantId = parsed;
                return true;

            default:
                tenantId = Guid.Empty;
                return false;
        }
    }
}
