using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Mlcp.Shared.Identity;

/// <summary>Configuration for MLCP's two multi-tenant registrations and their shared credential (ADR-015).</summary>
public sealed record MlcpIdentityOptions
{
    /// <summary>Application (client) id of the core registration. Config: <c>AzureAd:ClientId</c>.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Application (client) id of the Usage Insights registration (<c>Reports.Read.All</c>).
    /// Config: <c>AzureAd:UsageInsightsClientId</c>.
    /// </summary>
    public string? UsageInsightsClientId { get; init; }

    /// <summary>Name of the certificate in Key Vault. Both registrations use it. Config: <c>Mlcp:ClientCertificateName</c>.</summary>
    public string? CertificateName { get; init; }

    /// <summary>Key Vault URI holding the certificate. Config: <c>Mlcp:KeyVaultUri</c>.</summary>
    public Uri? KeyVaultUri { get; init; }

    /// <summary>
    /// A pre-loaded certificate, used locally and in tests. Owned by the caller: the token provider
    /// never disposes or reloads it.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>Core app client secret. Development only (CLAUDE.md rule 4). Config: <c>AzureAd:ClientSecret</c>.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Usage Insights client secret. Development only. Config: <c>AzureAd:UsageInsightsClientSecret</c>.</summary>
    public string? UsageInsightsClientSecret { get; init; }

    /// <summary>
    /// Sends the certificate chain (x5c) with the client assertion. Only needed when the
    /// registrations trust the certificate by subject name and issuer. Config:
    /// <c>Mlcp:Identity:SendCertificateChain</c>. Default false.
    /// </summary>
    public bool SendCertificateChain { get; init; }

    /// <summary>How long before expiry a cached token is considered stale.</summary>
    public TimeSpan RefreshSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A Key Vault certificate this close to expiry is re-fetched, to pick up a rotation.</summary>
    public TimeSpan CertificateReloadWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Minimum time between reload attempts while inside the reload window.</summary>
    public TimeSpan CertificateReloadCheckInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long all token acquisition for an app pauses after a platform credential failure (ADR-016).</summary>
    public TimeSpan PlatformCredentialPause { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>True when a production-grade credential source (Key Vault or a supplied certificate) is configured.</summary>
    public bool HasCertificateSource
        => ClientCertificate is not null || (KeyVaultUri is not null && !string.IsNullOrWhiteSpace(CertificateName));

    public string? ClientIdFor(MicrosoftApp app) => app switch
    {
        MicrosoftApp.Core => ClientId,
        MicrosoftApp.UsageInsights => UsageInsightsClientId,
        _ => null,
    };

    public string? ClientSecretFor(MicrosoftApp app) => app switch
    {
        MicrosoftApp.Core => ClientSecret,
        MicrosoftApp.UsageInsights => UsageInsightsClientSecret,
        _ => null,
    };

    /// <summary>Reads the options from the standard configuration keys.</summary>
    public static MlcpIdentityOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var keyVaultUri = configuration["Mlcp:KeyVaultUri"];

        return new MlcpIdentityOptions
        {
            ClientId = configuration["AzureAd:ClientId"] ?? string.Empty,
            UsageInsightsClientId = NullIfBlank(configuration["AzureAd:UsageInsightsClientId"]),
            CertificateName = NullIfBlank(configuration["Mlcp:ClientCertificateName"]),
            KeyVaultUri = string.IsNullOrWhiteSpace(keyVaultUri) ? null : new Uri(keyVaultUri),
            ClientSecret = NullIfBlank(configuration["AzureAd:ClientSecret"]),
            UsageInsightsClientSecret = NullIfBlank(configuration["AzureAd:UsageInsightsClientSecret"]),
            SendCertificateChain = bool.TryParse(configuration["Mlcp:Identity:SendCertificateChain"], out var send) && send,
        };
    }

    /// <summary>
    /// Returns the problems that make this configuration unusable outside Development. An empty
    /// list means the host may start.
    /// </summary>
    public IReadOnlyList<string> ValidateForProduction()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            problems.Add("AzureAd:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(UsageInsightsClientId))
        {
            problems.Add("AzureAd:UsageInsightsClientId is required (ADR-015).");
        }

        if (!HasCertificateSource)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"A certificate credential is required outside Development: set Mlcp:KeyVaultUri and Mlcp:ClientCertificateName."));
        }

        return problems;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
