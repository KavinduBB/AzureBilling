namespace Mlcp.Web.Infrastructure;

/// <summary>
/// Configuration a deployed web host refuses to start without.
/// </summary>
/// <remarks>
/// <para>
/// Every item here used to have a silent fallback — an in-memory cache, an on-disk key ring, an
/// email "sender" that wrote live tokens to the log, a wildcard host filter. Each fallback turns
/// a deployment mistake into a security or correctness problem that surfaces only in production
/// (sessions lost on restart, consent links in logs, links built from a spoofed Host). Outside
/// Development the host fails at startup instead, naming what is missing.
/// </para>
/// <para>
/// Development keeps the fallbacks so a developer can run the app with user-secrets alone.
/// </para>
/// </remarks>
public static class StartupRequirements
{
    /// <summary>The missing or unsafe settings, empty when the configuration is deployable.</summary>
    public static IReadOnlyList<string> FindProblems(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var problems = new List<string>();

        Require(configuration, "AzureAd:ClientId", problems);

        if (!configuration.GetSection("AzureAd:ClientCredentials").GetChildren().Any(c => !string.IsNullOrWhiteSpace(c["SourceType"])))
        {
            problems.Add("AzureAd:ClientCredentials:0:SourceType (the sign-in credential, e.g. KeyVault) is required.");
        }

        Require(configuration, "ConnectionStrings:Redis", problems, "the MSAL token cache and consent nonces must be shared");
        Require(configuration, "Mlcp:DataProtection:BlobUri", problems, "Data Protection keys must be shared and persistent");
        Require(configuration, "Mlcp:DataProtection:KeyUri", problems, "Data Protection keys must be encrypted with Key Vault");
        Require(configuration, "Mlcp:Email:Endpoint", problems, "consent request emails need a real sender");
        Require(configuration, "Mlcp:Email:SenderAddress", problems, "consent request emails need a real sender");
        Require(configuration, "Mlcp:Region", problems);
        Require(configuration, "Mlcp:Regions:DirectoryTableUri", problems, "the global tenant region directory (ADR-021)");

        if (!Uri.TryCreate(configuration["Mlcp:PublicBaseUrl"], UriKind.Absolute, out var publicBase)
            || publicBase.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("Mlcp:PublicBaseUrl must be this stack's https origin; emailed links are built from it.");
        }

        var allowedHosts = configuration["AllowedHosts"];

        if (string.IsNullOrWhiteSpace(allowedHosts)
            || allowedHosts.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("*"))
        {
            problems.Add("AllowedHosts must list this stack's host names; '*' is only acceptable in Development.");
        }

        return problems;
    }

    /// <summary>Throws when <paramref name="problems"/> is not empty.</summary>
    public static void ThrowIfAny(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The web host is not configured for deployment:" + Environment.NewLine + " - "
                + string.Join(Environment.NewLine + " - ", problems));
        }
    }

    private static void Require(IConfiguration configuration, string key, List<string> problems, string? why = null)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
        {
            problems.Add(why is null ? $"{key} is required." : $"{key} is required: {why}.");
        }
    }
}
