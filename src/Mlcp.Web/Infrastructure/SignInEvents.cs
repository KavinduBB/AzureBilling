using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Memory;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Tenancy;
using Mlcp.Web.Models;

namespace Mlcp.Web.Infrastructure;

/// <summary>
/// Work done once per interactive sign-in, inside the OpenID Connect handler.
/// </summary>
/// <remarks>
/// <para>
/// This is where "re-evaluated at every sign-in" (ADR-018) happens: the user record is created
/// or updated and Owner is re-derived from <c>wids</c> before the session cookie exists.
/// </para>
/// <para>
/// A tenant registered in another region is sent there before any cookie is issued on this
/// host (ADR-021).
/// </para>
/// <para>
/// The request scope is bound to the token's <c>tid</c> here, because the sign-in callback ends
/// inside the authentication middleware and never reaches <see cref="TenantResolutionMiddleware"/>.
/// The value is the one Microsoft.Identity.Web has just validated (CLAUDE.md rule 2).
/// </para>
/// </remarks>
public static class SignInEvents
{
    /// <summary>Chains MLCP's handlers after whatever Microsoft.Identity.Web installed.</summary>
    public static void Attach(OpenIdConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var previousTokenValidated = options.Events.OnTokenValidated;

        options.Events.OnTokenValidated = async context =>
        {
            await previousTokenValidated(context);

            if (context.Result is null)
            {
                await OnTokenValidatedAsync(context);
            }
        };
    }

    public static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        if (context.Properties?.Items.ContainsKey(RecentAuthentication.ForcedReauthenticationItem) == true)
        {
            identity.AddClaim(new Claim(
                RecentAuthentication.ReauthenticatedAtClaim,
                now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64));
        }

        if (!context.Principal.TryGetSignedInUser(out var user))
        {
            // TenantResolutionMiddleware refuses such a principal on its first request.
            return;
        }

        var deployment = services.GetRequiredService<DeploymentOptions>();
        var region = await RegionRoutingMiddleware.LookupAsync(
            services.GetRequiredService<IRegionDirectory>(),
            services.GetRequiredService<IMemoryCache>(),
            user.TenantId,
            context.HttpContext.RequestAborted);

        if (region is not null && !string.Equals(region, deployment.Region, StringComparison.OrdinalIgnoreCase))
        {
            var target = services.GetRequiredService<MlcpWebOptions>().RegionUrl(region, "/");

            if (target is not null)
            {
                context.HandleResponse();
                context.Response.Redirect(target.AbsoluteUri);
                return;
            }

            context.Fail($"The organisation is registered in region {region}, which is not reachable from here.");
            return;
        }

        services.GetRequiredService<TenantContext>().SetTenant(user.TenantId);

        await services.GetRequiredService<OnboardingService>()
            .RecordSignInAsync(user, deployment.Region, context.HttpContext.RequestAborted);
    }
}
