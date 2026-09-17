using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Mlcp.Application.Onboarding;
using Mlcp.Web.Models;

namespace Mlcp.Web.Infrastructure;

/// <summary>
/// Sends a signed-in user to the regional stack that holds their tenant (ADR-021).
/// </summary>
/// <remarks>
/// <para>
/// Runs after authentication. The tenant's region is looked up in the global directory (cached
/// for ten minutes; unknown answers for one). A tenant registered in another region is sent to
/// that region's host, same path, and signed out here: the auth cookie is host-only, so no
/// session is carried across regions and none is left behind.
/// </para>
/// <para>
/// A tenant not registered anywhere continues here; it is registered when an administrator
/// confirms the region at connection.
/// </para>
/// </remarks>
public sealed class RegionRoutingMiddleware
{
    public static TimeSpan KnownRegionCacheDuration { get; } = TimeSpan.FromMinutes(10);

    public static TimeSpan UnknownRegionCacheDuration { get; } = TimeSpan.FromMinutes(1);

    private readonly RequestDelegate _next;
    private readonly ILogger<RegionRoutingMiddleware> _logger;

    public RegionRoutingMiddleware(RequestDelegate next, ILogger<RegionRoutingMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(
        HttpContext context,
        IRegionDirectory directory,
        IMemoryCache cache,
        DeploymentOptions deployment,
        MlcpWebOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(options);

        if (!context.User.TryGetSignedInUser(out var user))
        {
            await _next(context);
            return;
        }

        var region = await LookupAsync(directory, cache, user.TenantId, context.RequestAborted);

        if (region is null || string.Equals(region, deployment.Region, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        await RedirectToRegionAsync(context, options, region, _logger);
    }

    /// <summary>The tenant's region from the cache or the directory.</summary>
    public static async Task<string?> LookupAsync(
        IRegionDirectory directory,
        IMemoryCache cache,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(cache);

        var key = CacheKey(tenantId);

        if (cache.TryGetValue(key, out RegionCacheEntry? cached) && cached is not null)
        {
            return cached.Region;
        }

        var region = await directory.GetRegionAsync(tenantId, cancellationToken);

        cache.Set(
            key,
            new RegionCacheEntry(region),
            region is null ? UnknownRegionCacheDuration : KnownRegionCacheDuration);

        return region;
    }

    /// <summary>Forgets a cached lookup, after this instance registers the tenant.</summary>
    public static void Invalidate(IMemoryCache cache, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(cache);
        cache.Remove(CacheKey(tenantId));
    }

    /// <summary>
    /// Signs the user out of this host and redirects to the same path on <paramref name="region"/>'s
    /// host. When that host is not configured the request is refused rather than served from
    /// the wrong region.
    /// </summary>
    public static async Task RedirectToRegionAsync(HttpContext context, MlcpWebOptions options, string region, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var target = options.RegionUrl(region, context.Request.Path + context.Request.QueryString);

        if (target is null)
        {
            logger.LogError("Tenant is registered in region {Region}, which has no configured host.", region);
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        await SignOutLocalSessionAsync(context);

        logger.LogInformation("Redirecting a user to region {Region}.", region);
        context.Response.Redirect(target.AbsoluteUri);
    }

    private static async Task SignOutLocalSessionAsync(HttpContext context)
    {
        var schemes = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();

        if (await schemes.GetSchemeAsync(CookieAuthenticationDefaults.AuthenticationScheme) is not null)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static string CacheKey(Guid tenantId) => $"region:{tenantId:N}";

    private sealed record RegionCacheEntry(string? Region);
}

public static class RegionRoutingMiddlewareExtensions
{
    /// <summary>Adds regional routing. Must run after authentication.</summary>
    public static IApplicationBuilder UseRegionRouting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RegionRoutingMiddleware>();
    }
}
