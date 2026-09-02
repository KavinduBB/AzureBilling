using Microsoft.Extensions.DependencyInjection;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Http;

namespace Mlcp.Integration.Graph;

public static class DependencyInjection
{
    public const string HttpClientName = "Graph";

    public static Uri DefaultBaseAddress { get; } = new("https://graph.microsoft.com/");

    /// <summary>
    /// Registers the Graph integration.
    /// </summary>
    /// <remarks>
    /// Automatic redirects are turned off. Usage report endpoints answer 302 with a
    /// pre-authenticated CSV URL, and the default handler would follow it while still carrying
    /// our <c>Authorization</c> header — sending an Entra bearer token to a storage endpoint
    /// that neither needs nor should receive it (docs/02-api-reference.md §1.2).
    /// </remarks>
    public static IServiceCollection AddGraphIntegration(this IServiceCollection services, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<GraphApiClient>(client =>
            {
                client.BaseAddress = baseAddress ?? DefaultBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(100);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.AddScoped<IGraphCapabilityProbe, GraphCapabilityProbe>();

        return services;
    }
}

/// <summary>
/// A <see cref="MicrosoftApiClient"/> bound to the Graph base address.
/// </summary>
/// <remarks>
/// A named subclass rather than a raw named client, so the Graph and ARM clients are distinct
/// types in the container. Injecting the wrong one would otherwise be a silent misconfiguration
/// that only surfaced as 404s from the wrong host.
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
