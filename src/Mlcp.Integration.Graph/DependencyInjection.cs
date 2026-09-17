using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Http;

namespace Mlcp.Integration.Graph;

public static class DependencyInjection
{
    public const string HttpClientName = "Graph";

    /// <summary>Configuration key overriding the Graph base address (tests, sovereign clouds).</summary>
    public const string BaseAddressKey = "Mlcp:MicrosoftApi:GraphBaseAddress";

    public static Uri DefaultBaseAddress { get; } = new("https://graph.microsoft.com/");

    /// <summary>Registers the Graph integration, reading the base address from <see cref="BaseAddressKey"/>.</summary>
    public static IServiceCollection AddGraphIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[BaseAddressKey];
        return services.AddGraphIntegration(string.IsNullOrWhiteSpace(configured) ? null : new Uri(configured));
    }

    /// <summary>
    /// Registers the Graph integration: the probe, the consent verifier and the typed client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HttpClient.Timeout"/> is infinite: the resilience pipeline's per-attempt timeout
    /// is the only timeout, so a slow attempt is retried rather than failing the whole call
    /// (ADR-016 rule 6).
    /// </para>
    /// <para>
    /// Automatic redirects are off. Usage report endpoints answer 302 with a pre-authenticated CSV
    /// URL, and following it would send our bearer token to a storage endpoint (docs/02 §1.2).
    /// </para>
    /// <para>
    /// <see cref="GraphConsentVerifier"/> depends on <see cref="ITenantOnboardingStore"/>, which the
    /// host registers with persistence.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddGraphIntegration(this IServiceCollection services, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<GraphApiClient>(client =>
            {
                client.BaseAddress = baseAddress ?? DefaultBaseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.AddScoped<IGraphCapabilityProbe, GraphCapabilityProbe>();
        services.AddScoped<IConsentVerifier, GraphConsentVerifier>();

        return services;
    }
}

/// <summary>
/// A <see cref="MicrosoftApiClient"/> bound to the Graph base address.
/// </summary>
/// <remarks>
/// A named subclass rather than a raw named client, so the Graph and ARM clients are distinct
/// types in the container and cannot be swapped silently.
/// </remarks>
public sealed class GraphApiClient : MicrosoftApiClient
{
    public GraphApiClient(
        HttpClient httpClient,
        Mlcp.Shared.Identity.ITenantTokenProvider tokenProvider,
        Mlcp.Shared.Resilience.MicrosoftApiExecutor executor)
        : base(httpClient, tokenProvider, executor)
    {
    }
}
