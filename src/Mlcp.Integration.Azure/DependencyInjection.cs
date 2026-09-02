using Microsoft.Extensions.DependencyInjection;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Http;

namespace Mlcp.Integration.Azure;

public static class DependencyInjection
{
    public static Uri DefaultBaseAddress { get; } = new("https://management.azure.com/");

    /// <summary>
    /// Registers the Azure Resource Manager integration: subscriptions, Billing, Cost
    /// Management and Consumption all share one host and one credential audience.
    /// </summary>
    public static IServiceCollection AddAzureIntegration(this IServiceCollection services, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<AzureApiClient>(client =>
            {
                client.BaseAddress = baseAddress ?? DefaultBaseAddress;

                // Cost Management's asynchronous operations answer quickly with a Location
                // header; the long wait is in polling, not in a single request.
                client.Timeout = TimeSpan.FromSeconds(100);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.AddScoped<IAzureCapabilityProbe, AzureCapabilityProbe>();
        services.AddScoped<IBillingCapabilityProbe, BillingCapabilityProbe>();

        return services;
    }
}

/// <summary>A <see cref="MicrosoftApiClient"/> bound to the Azure Resource Manager base address.</summary>
public sealed class AzureApiClient : MicrosoftApiClient
{
    public AzureApiClient(
        HttpClient httpClient,
        Mlcp.Shared.Identity.ITenantTokenProvider tokenProvider,
        Mlcp.Shared.Resilience.MicrosoftApiExecutor executor)
        : base(httpClient, tokenProvider, executor)
    {
    }
}
