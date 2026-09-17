using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Http;

namespace Mlcp.Integration.Azure;

public static class DependencyInjection
{
    /// <summary>Configuration key overriding the ARM base address (tests, sovereign clouds).</summary>
    public const string BaseAddressKey = "Mlcp:MicrosoftApi:ResourceManagerBaseAddress";

    public static Uri DefaultBaseAddress { get; } = new("https://management.azure.com/");

    /// <summary>Registers the ARM integration, reading the base address from <see cref="BaseAddressKey"/>.</summary>
    public static IServiceCollection AddAzureIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[BaseAddressKey];
        return services.AddAzureIntegration(string.IsNullOrWhiteSpace(configured) ? null : new Uri(configured));
    }

    /// <summary>
    /// Registers the Azure Resource Manager integration: subscriptions, Billing, Cost Management
    /// and Consumption share one host and one credential audience.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> is infinite; the resilience pipeline's per-attempt timeout
    /// is the only one (ADR-016 rule 6). The Service Bus enqueuer is registered separately with
    /// <see cref="Messaging.ServiceBusRegistration.AddMlcpServiceBusEnqueuer"/>.
    /// </remarks>
    public static IServiceCollection AddAzureIntegration(this IServiceCollection services, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<AzureApiClient>(client =>
            {
                client.BaseAddress = baseAddress ?? DefaultBaseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
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
