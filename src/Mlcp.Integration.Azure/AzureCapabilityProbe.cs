using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Costs;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Http;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Integration.Azure;

/// <summary>
/// Probes Azure Resource Manager for subscriptions, their agreement type, and whether Cost
/// Management answers at subscription scope (rung 3) and at the root management group (rung 2).
/// </summary>
/// <remarks>
/// <para>
/// Listing subscriptions (Reader) and querying costs (Cost Management Reader) are separate grants
/// and are probed and classified separately (docs/03 §4.1, ADR-016 rule 1).
/// </para>
/// <para>
/// The root management group query is only tried when a classified subscription is EA or MOSP:
/// management groups are not supported for MCA or CSP subscriptions (ADR-017).
/// </para>
/// </remarks>
public sealed class AzureCapabilityProbe : IAzureCapabilityProbe
{
    internal const string SubscriptionsUri = "subscriptions?api-version=2022-12-01";

    /// <summary>Cap on billing-property lookups per discovery; the full set is the sync's job.</summary>
    internal const int MaxSubscriptionsToClassify = 10;

    private readonly AzureApiClient _client;
    private readonly ILogger<AzureCapabilityProbe> _logger;

    public AzureCapabilityProbe(AzureApiClient client, ILogger<AzureCapabilityProbe> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal static string BillingPropertyUri(Guid subscriptionId) => string.Create(
        CultureInfo.InvariantCulture,
        $"subscriptions/{subscriptionId:D}/providers/Microsoft.Billing/billingProperty/default?api-version=2024-04-01");

    public async Task<ArmProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listing = await _client.ProbeAsync<ArmCollection<SubscriptionResource>>(
            new MicrosoftRequest(context.TenantId, MicrosoftProvider.ResourceManager, TokenAudience.ResourceManager, SubscriptionsUri)
            {
                ConsentCallbackUtc = context.ConsentCallbackUtc,
            },
            cancellationToken).ConfigureAwait(false);

        var listOutcome = ProbeCallOutcome.From(listing);

        if (!listing.Succeeded)
        {
            return new ArmProbeResult(
                [],
                CostManagementQueryable: false,
                SubscriptionList: listOutcome,
                SubscriptionCostQuery: ProbeCallOutcome.NotAttempted,
                RootManagementGroupCostQuery: ProbeCallOutcome.NotAttempted);
        }

        var visible = (listing.Value?.Value ?? [])
            .Where(s => Guid.TryParse(s.SubscriptionId, out _))
            .ToList();

        var probes = new List<AzureSubscriptionProbe>(visible.Count);

        foreach (var subscription in visible)
        {
            var subscriptionId = Guid.Parse(subscription.SubscriptionId!);
            var displayName = subscription.DisplayName ?? subscriptionId.ToString();

            if (probes.Count >= MaxSubscriptionsToClassify)
            {
                probes.Add(new AzureSubscriptionProbe(
                    subscriptionId, displayName, AgreementType.NotDiscovered, IsAzurePlan: null, BillingProperty: ProbeCallOutcome.NotAttempted));
                continue;
            }

            probes.Add(await ClassifyAsync(context, subscriptionId, displayName, cancellationToken).ConfigureAwait(false));
        }

        var costQuery = ProbeCallOutcome.NotAttempted;
        var rootQuery = ProbeCallOutcome.NotAttempted;

        if (probes.Count > 0)
        {
            costQuery = ProbeCallOutcome.From(await _client.ProbeAsync(
                CostQueryProbe.Request(context.TenantId, CostScopeResolver.SubscriptionScope(probes[0].SubscriptionId), context.ConsentCallbackUtc),
                cancellationToken).ConfigureAwait(false));

            if (probes.Any(p => p.AgreementType is AgreementType.Ea or AgreementType.Mosa))
            {
                rootQuery = ProbeCallOutcome.From(await _client.ProbeAsync(
                    CostQueryProbe.Request(context.TenantId, CostScopeResolver.RootManagementGroupScope(context.TenantId), context.ConsentCallbackUtc),
                    cancellationToken).ConfigureAwait(false));
            }
        }

        _logger.LogInformation(
            "ARM probe for tenant {TenantId}: {SubscriptionCount} subscription(s), subscription cost query {CostQuery}, root management group query {RootQuery}.",
            context.TenantId,
            probes.Count,
            costQuery.Describe(),
            rootQuery.Describe());

        return new ArmProbeResult(
            probes,
            CostManagementQueryable: costQuery.Succeeded,
            SubscriptionList: listOutcome,
            SubscriptionCostQuery: costQuery,
            RootManagementGroupCostQuery: rootQuery,
            TotalSubscriptionCount: probes.Count);
    }

    private async Task<AzureSubscriptionProbe> ClassifyAsync(
        ProbeContext context,
        Guid subscriptionId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var response = await _client.ProbeAsync<BillingPropertyResource>(
            new MicrosoftRequest(context.TenantId, MicrosoftProvider.AzureBilling, TokenAudience.ResourceManager, BillingPropertyUri(subscriptionId))
            {
                ConsentCallbackUtc = context.ConsentCallbackUtc,
            },
            cancellationToken).ConfigureAwait(false);

        var outcome = ProbeCallOutcome.From(response);

        if (!response.Succeeded)
        {
            return new AzureSubscriptionProbe(subscriptionId, displayName, AgreementType.NotDiscovered, IsAzurePlan: null, BillingProperty: outcome);
        }

        var properties = response.Value?.Properties;
        var agreement = MapAgreementType(properties?.BillingAccountAgreementType);

        return new AzureSubscriptionProbe(
            subscriptionId,
            displayName,
            agreement,
            IsAzurePlan(agreement, properties?.BillingProfileId),
            properties?.BillingProfileId,
            outcome);
    }

    /// <summary>
    /// Whether the subscription is on an Azure plan rather than the classic CSP offer.
    /// </summary>
    /// <remarks>
    /// A partner (MPA) subscription with no billing profile is the classic CSP offer, which exposes
    /// no cost data (docs/01 §5.8). Any other recognised agreement is not classic CSP. An
    /// unrecognised or missing agreement is unknown, and stays null (CLAUDE.md rule 11).
    /// </remarks>
    internal static bool? IsAzurePlan(AgreementType agreement, string? billingProfileId) => agreement switch
    {
        AgreementType.Mpa => !string.IsNullOrWhiteSpace(billingProfileId),
        AgreementType.Mca or AgreementType.Ea or AgreementType.Mosa => true,
        _ => null,
    };

    /// <summary>
    /// Maps Microsoft's agreement strings onto our enum. Unrecognised values map to
    /// <see cref="AgreementType.Unknown"/>, never to a guess.
    /// </summary>
    internal static AgreementType MapAgreementType(string? agreementType) => agreementType switch
    {
        "MicrosoftCustomerAgreement" => AgreementType.Mca,
        "EnterpriseAgreement" => AgreementType.Ea,
        "MicrosoftPartnerAgreement" => AgreementType.Mpa,
        "MicrosoftOnlineServicesProgram" => AgreementType.Mosa,
        null or "" => AgreementType.NotDiscovered,
        _ => AgreementType.Unknown,
    };

    internal sealed record ArmCollection<T>
    {
        [JsonPropertyName("value")]
        public IReadOnlyList<T>? Value { get; init; }

        [JsonPropertyName("nextLink")]
        public string? NextLink { get; init; }
    }

    internal sealed record SubscriptionResource
    {
        public string? SubscriptionId { get; init; }

        public string? DisplayName { get; init; }

        public string? State { get; init; }
    }

    internal sealed record BillingPropertyResource
    {
        public BillingPropertyProperties? Properties { get; init; }
    }

    internal sealed record BillingPropertyProperties
    {
        public string? BillingAccountAgreementType { get; init; }

        public string? BillingProfileId { get; init; }

        public string? InvoiceSectionId { get; init; }

        public string? CostCenter { get; init; }

        public string? SubscriptionBillingStatus { get; init; }
    }
}
