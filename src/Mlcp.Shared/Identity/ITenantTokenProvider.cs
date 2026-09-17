namespace Mlcp.Shared.Identity;

/// <summary>The Microsoft resource a token is being requested for.</summary>
public enum TokenAudience
{
    Unknown = 0,

    /// <summary>Microsoft Graph — licences and directory, called as the core app.</summary>
    Graph = 1,

    /// <summary>Azure Resource Manager — Billing, Cost Management, Consumption, Advisor.</summary>
    ResourceManager = 2,

    /// <summary>
    /// Microsoft Graph usage reports, called as the separate Usage Insights app (ADR-015). The
    /// token has the same scope as <see cref="Graph"/> but is issued to a different client.
    /// </summary>
    GraphReports = 3,
}

/// <summary>
/// The multi-tenant app registration a token is issued to (ADR-015). Each has its own consent
/// in the customer tenant, so each fails and recovers independently.
/// </summary>
public enum MicrosoftApp
{
    Unknown = 0,

    /// <summary>MLCP core: sign-in, the universal floor, and the RBAC/billing-role principal.</summary>
    Core = 1,

    /// <summary>MLCP Usage Insights: <c>Reports.Read.All</c> only (tier 2).</summary>
    UsageInsights = 2,
}

/// <summary>
/// Acquires app-only access tokens for a specific customer tenant.
/// </summary>
/// <remarks>
/// <para>
/// Every sync call is made with a token issued by the <em>customer's</em> tenant authority
/// (<c>https://login.microsoftonline.com/{tid}/oauth2/v2.0/token</c>) against one of our two
/// registrations' credential. Tokens are cached per (tenant, app, audience).
/// </para>
/// <para>
/// Failures are reported as <see cref="TokenAcquisitionException"/> carrying the raw token-endpoint
/// error. The provider does not decide what a failure means for the tenant: that needs the
/// consent propagation window, which only the caller knows, so classification happens once in
/// <see cref="Mlcp.Shared.Resilience.MicrosoftFailureClassifier"/> (ADR-016).
/// </para>
/// </remarks>
public interface ITenantTokenProvider
{
    /// <summary>Returns a bearer token for <paramref name="tenantId"/> and <paramref name="audience"/>.</summary>
    /// <exception cref="TokenAcquisitionException">Microsoft refused or could not issue the token.</exception>
    Task<string> GetAccessTokenAsync(Guid tenantId, TokenAudience audience, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the cached token (and the cached credential, whose own cache would otherwise hand the
    /// same token back) for one (tenant, app, audience). Called on every 401/403 so a role granted
    /// since the token was issued is picked up on the next attempt (ADR-016 rule 4).
    /// </summary>
    void Evict(Guid tenantId, MicrosoftApp app, TokenAudience audience);
}

/// <summary>Scope and app mapping for the audiences the platform calls.</summary>
public static class TokenScopes
{
    public const string Graph = "https://graph.microsoft.com/.default";

    public const string ResourceManager = "https://management.azure.com/.default";

    public static string For(TokenAudience audience) => audience switch
    {
        TokenAudience.Graph or TokenAudience.GraphReports => Graph,
        TokenAudience.ResourceManager => ResourceManager,
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "No scope is defined for this audience."),
    };

    /// <summary>Which registration a token for <paramref name="audience"/> is issued to (ADR-015).</summary>
    public static MicrosoftApp AppFor(TokenAudience audience) => audience switch
    {
        TokenAudience.GraphReports => MicrosoftApp.UsageInsights,
        TokenAudience.Graph or TokenAudience.ResourceManager => MicrosoftApp.Core,
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "No app is defined for this audience."),
    };
}
