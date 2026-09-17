using System.Globalization;
using System.Security.Claims;
using Microsoft.Identity.Web;
using Mlcp.Application.Onboarding;

namespace Mlcp.Web.Infrastructure;

/// <summary>
/// Builds a <see cref="SignedInUser"/> from the validated token claims.
/// </summary>
/// <remarks>
/// Every field comes from the token, never from the request. The tenant id in particular is the
/// validated <c>tid</c> claim: the same value <see cref="TenantResolutionMiddleware"/> binds the
/// scope to, so the identity the application service acts on and the tenant the database is
/// confined to can never disagree (CLAUDE.md rule 2). Admin status is read from the live
/// <c>wids</c> claim on every call (ADR-018).
/// </remarks>
public static class SignedInUserAccessor
{
    public static bool TryGetSignedInUser(this ClaimsPrincipal? principal, out SignedInUser user)
    {
        user = null!;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var rawTenantId = principal.FindFirstValue(TenantResolutionMiddleware.TenantIdClaim)
            ?? principal.FindFirstValue(TenantResolutionMiddleware.TenantIdClaimLongForm);

        var rawObjectId = principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

        if (!Guid.TryParse(rawTenantId, out var tenantId) || tenantId == Guid.Empty)
        {
            return false;
        }

        if (!Guid.TryParse(rawObjectId, out var objectId) || objectId == Guid.Empty)
        {
            return false;
        }

        var upn = principal.GetDisplayName()
            ?? principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue(ClaimTypes.Upn)
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? objectId.ToString();

        var displayName = principal.FindFirstValue("name")
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? upn;

        user = new SignedInUser(tenantId, objectId, upn, displayName, principal.IsDirectoryAdmin());
        return true;
    }

    /// <summary>
    /// True when the current token's <c>wids</c> claim holds a role that can grant tenant-wide
    /// consent (ADR-018).
    /// </summary>
    public static bool IsDirectoryAdmin(this ClaimsPrincipal? principal)
        => principal is not null
            && AdminRoles.CanGrantTenantWideConsent(
                principal.FindAll(AdminRoles.DirectoryRolesClaim).Select(c => c.Value));

    /// <summary>
    /// True when the token's <c>roles</c> claim carries the <c>SubscriptionManager</c> app role
    /// (ADR-023). Not used to authorise anything until Phase 5.
    /// </summary>
    public static bool HasSubscriptionManagerRole(this ClaimsPrincipal? principal)
        => principal is not null
            && EntraAppRoles.HasSubscriptionManager(
                principal.FindAll(EntraAppRoles.RolesClaim)
                    .Concat(principal.FindAll(ClaimTypes.Role))
                    .Select(c => c.Value));

    /// <summary>
    /// When the person last entered credentials, as far as MLCP can tell: the ID token's
    /// <c>auth_time</c> when present, or the time of a forced re-authentication MLCP asked for.
    /// The later of the two wins.
    /// </summary>
    public static DateTimeOffset? GetLastAuthenticationTime(this ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        var times = new[]
        {
            ReadUnixTime(principal.FindFirstValue(RecentAuthentication.AuthTimeClaim)),
            ReadUnixTime(principal.FindFirstValue(RecentAuthentication.ReauthenticatedAtClaim)),
        };

        return times.Where(t => t is not null).DefaultIfEmpty(null).Max();
    }

    private static DateTimeOffset? ReadUnixTime(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
