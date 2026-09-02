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
/// confined to can never disagree (CLAUDE.md rule 2).
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

        user = new SignedInUser(tenantId, objectId, upn, displayName);
        return true;
    }
}
