using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Mlcp.Web.Infrastructure;

/// <summary>
/// "Did this person enter their credentials in the last 15 minutes?" — the second gate on
/// disconnect and cancel-disconnect (ADR-018).
/// </summary>
/// <remarks>
/// <para>
/// A session cookie can be days old. Destroying an organisation's data on the strength of one is
/// what step-up authentication exists to prevent, so the destructive actions challenge again
/// with <c>prompt=login</c> and <c>max_age</c> unless a recent authentication is proven.
/// </para>
/// <para>
/// Proof is the ID token's <c>auth_time</c> (an optional claim on the registration), or — when
/// that claim is absent — a marker MLCP stamps at sign-in only when the sign-in answered one of
/// its own <c>prompt=login</c> challenges. The marker travels in the protected OIDC state, so a
/// client cannot forge it.
/// </para>
/// </remarks>
public static class RecentAuthentication
{
    /// <summary>The OIDC <c>auth_time</c> claim (seconds since the epoch).</summary>
    public const string AuthTimeClaim = "auth_time";

    /// <summary>Stamped at sign-in when the sign-in answered a forced re-authentication.</summary>
    public const string ReauthenticatedAtClaim = "mlcp_reauth_time";

    /// <summary>The authentication-properties item that marks a forced re-authentication.</summary>
    public const string ForcedReauthenticationItem = ".mlcp.forced_reauth";

    /// <summary>How recent the credential entry must be.</summary>
    public static TimeSpan MaxAge { get; } = TimeSpan.FromMinutes(15);

    /// <summary>True when <paramref name="principal"/> authenticated within <see cref="MaxAge"/>.</summary>
    public static bool IsRecent(ClaimsPrincipal? principal, DateTimeOffset nowUtc)
        => principal.GetLastAuthenticationTime() is { } at
            && at <= nowUtc + TimeSpan.FromMinutes(5) // tolerate modest clock skew
            && nowUtc - at <= MaxAge;

    /// <summary>
    /// Challenge properties that force Entra to ask for credentials again, returning to
    /// <paramref name="redirectUri"/>.
    /// </summary>
    public static OpenIdConnectChallengeProperties ChallengeProperties(string redirectUri)
    {
        var properties = new OpenIdConnectChallengeProperties
        {
            RedirectUri = redirectUri,
            Prompt = "login",
            MaxAge = MaxAge,
        };

        properties.Items[ForcedReauthenticationItem] = "1";
        return properties;
    }
}
