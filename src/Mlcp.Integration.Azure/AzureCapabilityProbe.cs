using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Integration.Azure;

/// <summary>
/// Probes Azure Resource Manager for subscriptions, their agreement type, and whether Cost
/// Management will actually answer.
/// </summary>
/// <remarks>
/// <para>
/// Listing subscriptions and querying costs are separate permissions and are probed separately.
/// Reader lists subscriptions; Cost Management Reader answers queries. Customers routinely
/// grant one and not the other, and the checklist has to be able to say which
/// (docs/03-architecture.md §4.1).
/// </para>
/// <para>
/// The cost probe is a single query at the smallest possible shape — one subscription, month
/// to date, no grouping — because Cost Management is rate limited at roughly four calls per
/// minute per scope and this runs interactively.
/// </para>
/// </remarks>
public sealed class AzureCapabilityProbe : IAzureCapabilityProbe
{
    private const string SubscriptionsUri = "subscriptions?api-version=2022-12-01";

    private static string BillingPropertyUri(Guid subscriptionId) => string.Create(
        CultureInfo.InvariantCulture,
        $"subscriptions/{subscriptionId}/providers/Microsoft.Billing/billingProperty/default?api-version=2024-04-01");

    private static string CostQueryUri(Guid subscriptionId) => string.Create(
        CultureInfo.InvariantCulture,
        $"subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2024-08-01");

    /// <summary>Cap on how many subscriptions get a billing-property lookup during onboarding.</summary>
    /// <remarks>
    /// A large enterprise can hold hundreds. The agreement type is a tenant-level fact in
    /// practice, so a sample answers the classification question; the full set is established
    /// later by the scheduled sync rather than while a user waits on a page.
    /// </remarks>
    private const int MaxSubscriptionsToClassify = 10;

    private readonly AzureApiClient _client;
    private readonly ILogger<AzureCapabilityProbe> _logger;

    public AzureCapabilityProbe(AzureApiClient client, ILogger<AzureCapabilityProbe> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ArmProbeResult> ProbeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var listing = await _client.GetJsonOrDefaultAsync<ArmCollection<SubscriptionResource>>(
            tenantId, MicrosoftProvider.ResourceManager, TokenAudience.ResourceManager, SubscriptionsUri, cancellationToken)
            .ConfigureAwait(false);

        if (listing?.Value is not { Count: > 0 } subscriptions)
        {
            return ArmProbeResult.NotReachable;
        }

        var probes = new List<AzureSubscriptionProbe>(subscriptions.Count);

        foreach (var subscription in subscriptions)
        {
            if (!Guid.TryParse(subscription.SubscriptionId, out var subscriptionId))
            {
                continue;
            }

            var (agreementType, isAzurePlan) = probes.Count < MaxSubscriptionsToClassify
                ? await ClassifyAsync(tenantId, subscriptionId, cancellationToken).ConfigureAwait(false)
                : (AgreementType.NotDiscovered, true);

            probes.Add(new AzureSubscriptionProbe(
                subscriptionId,
                subscription.DisplayName ?? subscriptionId.ToString(),
                agreementType,
                isAzurePlan));
        }

        var costQueryable = probes.Count > 0
            && await CanQueryCostAsync(tenantId, probes[0].SubscriptionId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "ARM probe for tenant {TenantId}: {SubscriptionCount} subscription(s), cost queryable {CostQueryable}.",
            tenantId,
            probes.Count,
            costQueryable);

        return new ArmProbeResult(probes, costQueryable);
    }

    private async Task<(AgreementType AgreementType, bool IsAzurePlan)> ClassifyAsync(
        Guid tenantId,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        var uri = BillingPropertyUri(subscriptionId);

        var property = await _client.GetJsonOrDefaultAsync<BillingPropertyResource>(
            tenantId, MicrosoftProvider.ResourceManager, TokenAudience.ResourceManager, uri, cancellationToken)
            .ConfigureAwait(false);

        var agreement = MapAgreementType(property?.Properties?.BillingAccountAgreementType);

        // A CSP subscription with no billing profile is the classic offer rather than an Azure
        // Plan, and exposes no cost data at all until the customer migrates (docs/01 §5.8).
        var isAzurePlan = agreement != AgreementType.Mpa
            || !string.IsNullOrWhiteSpace(property?.Properties?.BillingProfileId);

        return (agreement, isAzurePlan);
    }

    private async Task<bool> CanQueryCostAsync(Guid tenantId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var uri = CostQueryUri(subscriptionId);

        var body = new
        {
            type = "ActualCost",
            timeframe = "MonthToDate",
            dataset = new
            {
                granularity = "None",
                aggregation = new
                {
                    totalCost = new { name = "Cost", function = "Sum" },
                },
            },
        };

        using var response = await _client.PostJsonAsync(
            tenantId, MicrosoftProvider.CostManagement, TokenAudience.ResourceManager, uri, body, cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Maps Microsoft's agreement strings onto our enum.
    /// </summary>
    /// <remarks>
    /// An unrecognised value maps to <see cref="AgreementType.Unknown"/>, never to a guess.
    /// Guessing MCA for an unfamiliar string would promise a customer prices and lifecycle
    /// operations that then fail.
    /// </remarks>
    internal static AgreementType MapAgreementType(string? agreementType) => agreementType switch
    {
        "MicrosoftCustomerAgreement" => AgreementType.Mca,
        "EnterpriseAgreement" => AgreementType.Ea,
        "MicrosoftPartnerAgreement" => AgreementType.Mpa,
        "MicrosoftOnlineServicesProgram" => AgreementType.Mosa,
        null or "" => AgreementType.NotDiscovered,
        _ => AgreementType.Unknown,
    };

    private sealed record ArmCollection<T>
    {
        [JsonPropertyName("value")]
        public IReadOnlyList<T>? Value { get; init; }

        [JsonPropertyName("nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed record SubscriptionResource
    {
        public string? SubscriptionId { get; init; }

        public string? DisplayName { get; init; }

        public string? State { get; init; }
    }

    private sealed record BillingPropertyResource
    {
        public BillingPropertyProperties? Properties { get; init; }
    }

    private sealed record BillingPropertyProperties
    {
        public string? BillingAccountAgreementType { get; init; }

        public string? BillingProfileId { get; init; }

        public string? InvoiceSectionId { get; init; }

        public string? CostCenter { get; init; }

        public string? SubscriptionBillingStatus { get; init; }
    }
}
