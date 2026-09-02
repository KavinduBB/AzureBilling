using System.Security.Claims;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Web.Infrastructure;

/// <summary>
/// Binds the request scope to the tenant named by the signed-in principal.
/// </summary>
/// <remarks>
/// <para>
/// The tenant is taken from the <c>tid</c> claim (or its <c>http://schemas.microsoft.com/…
/// /tenantid</c> long form), which Microsoft.Identity.Web has already validated as part of
/// token validation. Nothing else is consulted — not a route value, header, query string or
/// body — because any of those would let a caller choose their own tenant and reduce four
/// isolation layers to one trusted input (CLAUDE.md rule 2).
/// </para>
/// <para>
/// Anonymous requests leave the scope unbound. That is deliberate: an unbound scope matches no
/// rows through the query filter and no rows through row-level security, so a missing sign-in
/// degrades to an empty result rather than a leak.
/// </para>
/// </remarks>
public sealed class TenantResolutionMiddleware
{
    /// <summary>The short claim type issued in v2.0 tokens.</summary>
    public const string TenantIdClaim = "tid";

    /// <summary>The long claim type ASP.NET Core maps some tokens onto.</summary>
    public const string TenantIdClaimLongForm = "http://schemas.microsoft.com/identity/claims/tenantid";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (TryGetTenantId(context.User, out var tenantId))
        {
            tenantContext.SetTenant(tenantId);
        }
        else if (context.User.Identity?.IsAuthenticated == true)
        {
            // Authenticated but no usable tid: a malformed or unexpected token. Refuse rather
            // than continue unbound, because the user would silently see an empty application.
            _logger.LogWarning("Authenticated principal carries no usable tenant id claim.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }

    private static bool TryGetTenantId(ClaimsPrincipal? principal, out Guid tenantId)
    {
        tenantId = Guid.Empty;

        var raw = principal?.FindFirstValue(TenantIdClaim) ?? principal?.FindFirstValue(TenantIdClaimLongForm);

        return !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out tenantId) && tenantId != Guid.Empty;
    }
}

public static class TenantResolutionMiddlewareExtensions
{
    /// <summary>
    /// Adds tenant resolution. Must run after authentication, so the principal exists, and
    /// before anything that touches the database.
    /// </summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
