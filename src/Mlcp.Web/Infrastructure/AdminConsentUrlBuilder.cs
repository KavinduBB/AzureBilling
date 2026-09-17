using System.Text;

namespace Mlcp.Web.Infrastructure;

/// <summary>Settings for the Entra admin consent redirect.</summary>
public sealed record AdminConsentOptions
{
    /// <summary>The core MLCP registration (tier 1).</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>The separate "MLCP Usage Insights" registration (tier 2, ADR-015). Empty when not configured.</summary>
    public string UsageInsightsClientId { get; init; } = string.Empty;

    /// <summary>
    /// The admin consent endpoint requires <c>/.default</c>, which grants every application
    /// permission configured on the registration. That is why tier 2 is a separate registration
    /// rather than a narrower scope on this one (ADR-015).
    /// </summary>
    public string Scope { get; init; } = "https://graph.microsoft.com/.default";

    public string Instance { get; init; } = "https://login.microsoftonline.com/";
}

/// <summary>
/// Builds the admin consent URLs
/// (<see href="https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent"/>).
/// </summary>
/// <remarks>
/// The <c>state</c> comes from <see cref="ConsentStateProtector"/>; this type only assembles the
/// URL. The authority is the caller's own tenant rather than <c>/organizations</c>, so an admin
/// signed into several directories consents in the one MLCP is acting for.
/// </remarks>
public sealed class AdminConsentUrlBuilder
{
    private readonly AdminConsentOptions _options;

    public AdminConsentUrlBuilder(AdminConsentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>True when the Usage Insights registration is configured.</summary>
    public bool HasUsageInsights => !string.IsNullOrWhiteSpace(_options.UsageInsightsClientId);

    /// <summary>The URL for tier 1: connect the organisation.</summary>
    public string Build(Guid tenantId, string redirectUri, string state)
        => BuildFor(_options.ClientId, tenantId, redirectUri, state);

    /// <summary>The URL for tier 2: usage insights, on its own registration.</summary>
    /// <exception cref="InvalidOperationException">The Usage Insights registration is not configured.</exception>
    public string BuildUsageInsights(Guid tenantId, string redirectUri, string state)
    {
        if (!HasUsageInsights)
        {
            throw new InvalidOperationException("AzureAd:UsageInsightsClientId is not configured.");
        }

        return BuildFor(_options.UsageInsightsClientId, tenantId, redirectUri, state);
    }

    private string BuildFor(string clientId, Guid tenantId, string redirectUri, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        return new StringBuilder(_options.Instance.TrimEnd('/'))
            .Append('/')
            .Append(tenantId.ToString("D"))
            .Append("/v2.0/adminconsent?client_id=")
            .Append(Uri.EscapeDataString(clientId))
            .Append("&scope=")
            .Append(Uri.EscapeDataString(_options.Scope))
            .Append("&redirect_uri=")
            .Append(Uri.EscapeDataString(redirectUri))
            .Append("&state=")
            .Append(Uri.EscapeDataString(state))
            .ToString();
    }
}
