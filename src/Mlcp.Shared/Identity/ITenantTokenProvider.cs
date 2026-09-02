namespace Mlcp.Shared.Identity;

/// <summary>The Microsoft resource a token is being requested for.</summary>
public enum TokenAudience
{
    Unknown = 0,

    /// <summary>Microsoft Graph — licences, directory, usage reports.</summary>
    Graph = 1,

    /// <summary>Azure Resource Manager — Billing, Cost Management, Consumption, Advisor.</summary>
    ResourceManager = 2,
}

/// <summary>
/// Acquires app-only access tokens for a specific customer tenant.
/// </summary>
/// <remarks>
/// <para>
/// The platform is a multi-tenant application, so every sync call is made with a token issued
/// by the <em>customer's</em> tenant authority
/// (<c>https://login.microsoftonline.com/{tid}/oauth2/v2.0/token</c>) against our own
/// application's credential. There is no single token that works across tenants.
/// </para>
/// <para>
/// The credential is a certificate held in Key Vault, never a client secret
/// (docs/02-api-reference.md §4, CLAUDE.md rule 4). User tokens never pass through here: those
/// live only in the MSAL distributed cache and are never persisted by us (rule 3).
/// </para>
/// </remarks>
public interface ITenantTokenProvider
{
    /// <summary>
    /// Returns a bearer token for <paramref name="tenantId"/> and <paramref name="audience"/>.
    /// </summary>
    /// <exception cref="Mlcp.Shared.Resilience.NeedsReconsentException">
    /// The tenant's grant is gone — consent revoked, or the application removed from the
    /// customer's directory. Retrying cannot help.
    /// </exception>
    Task<string> GetAccessTokenAsync(Guid tenantId, TokenAudience audience, CancellationToken cancellationToken);
}

/// <summary>Scope constants for the audiences the platform calls.</summary>
public static class TokenScopes
{
    public const string Graph = "https://graph.microsoft.com/.default";

    public const string ResourceManager = "https://management.azure.com/.default";

    public static string For(TokenAudience audience) => audience switch
    {
        TokenAudience.Graph => Graph,
        TokenAudience.ResourceManager => ResourceManager,
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "No scope is defined for this audience."),
    };
}
