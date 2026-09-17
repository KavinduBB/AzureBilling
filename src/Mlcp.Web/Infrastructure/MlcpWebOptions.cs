namespace Mlcp.Web.Infrastructure;

/// <summary>Deployment settings the web host needs beyond the Entra registration.</summary>
public sealed record MlcpWebOptions
{
    /// <summary>
    /// This stack's own public origin (<c>Mlcp:PublicBaseUrl</c>), for example
    /// <c>https://eu.app.example.com/</c>. Links that leave the request — the admin consent email
    /// above all — are built from this, never from the request's Host header, which a client
    /// controls.
    /// </summary>
    public Uri? PublicBaseUrl { get; init; }

    /// <summary>
    /// Raw URL of the compiled customer RBAC ARM template (<c>Mlcp:CustomerRbacTemplateUrl</c>),
    /// used for Guide B's "Deploy to Azure" button. The portal cannot deploy Bicep directly, so
    /// this must be the JSON build of <c>infra/customer-rbac.bicep</c>.
    /// </summary>
    public Uri? CustomerRbacTemplateUrl { get; init; }

    /// <summary>
    /// Client id of the separate "MLCP Usage Insights" registration (<c>AzureAd:UsageInsightsClientId</c>),
    /// which holds <c>Reports.Read.All</c> and is consented separately (ADR-015).
    /// </summary>
    public string? UsageInsightsClientId { get; init; }

    /// <summary>
    /// Regional hosts by region code (<c>Mlcp:Regions:Hosts:{region}</c>), for example
    /// <c>eu → eu.app.example.com</c> (ADR-021).
    /// </summary>
    public IReadOnlyDictionary<string, string> RegionHosts { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Human-readable region names (<c>Mlcp:Regions:Names:{region}</c>), for example <c>eu → the EU</c>.</summary>
    public IReadOnlyDictionary<string, string> RegionNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The display name of a region, falling back to its code.</summary>
    public string RegionName(string region)
        => RegionNames.TryGetValue(region, out var name) && !string.IsNullOrWhiteSpace(name) ? name : region;

    /// <summary>
    /// The absolute URL of <paramref name="pathAndQuery"/> on <paramref name="region"/>'s host, or
    /// null when that region has no configured host.
    /// </summary>
    public Uri? RegionUrl(string region, string pathAndQuery)
    {
        if (!RegionHosts.TryGetValue(region, out var host) || string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var authority = host.Contains("://", StringComparison.Ordinal) ? host : "https://" + host;

        if (!Uri.TryCreate(authority, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var relative = string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery;
        return Uri.TryCreate(new Uri(baseUri.GetLeftPart(UriPartial.Authority)), relative, out var target) ? target : null;
    }

    /// <summary>
    /// The Guide B "Deploy to Azure" link, or null when no template URL is configured
    /// (<see href="https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deploy-to-azure-button"/>).
    /// </summary>
    public string? DeployToAzureUrl => CustomerRbacTemplateUrl is null
        ? null
        : "https://portal.azure.com/#create/Microsoft.Template/uri/"
            + Uri.EscapeDataString(CustomerRbacTemplateUrl.AbsoluteUri);

    public static MlcpWebOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new MlcpWebOptions
        {
            PublicBaseUrl = AbsoluteUri(configuration["Mlcp:PublicBaseUrl"]),
            CustomerRbacTemplateUrl = AbsoluteUri(configuration["Mlcp:CustomerRbacTemplateUrl"]),
            UsageInsightsClientId = NullIfBlank(configuration["AzureAd:UsageInsightsClientId"]),
            RegionHosts = ReadMap(configuration.GetSection("Mlcp:Regions:Hosts")),
            RegionNames = ReadMap(configuration.GetSection("Mlcp:Regions:Names")),
        };
    }

    private static Dictionary<string, string> ReadMap(IConfigurationSection section)
        => section.GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);

    private static Uri? AbsoluteUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
