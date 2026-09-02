using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Integration.Graph;

/// <summary>
/// Probes Microsoft Graph to establish the universal floor and detect partner management.
/// </summary>
/// <remarks>
/// <para>
/// Each call is deliberately the cheapest one that answers its question. This runs on the
/// interactive onboarding path, which is the one place CLAUDE.md rule 5 permits calling
/// Microsoft during a request, so it must not become a data load.
/// </para>
/// <para>
/// Only endpoints listed in docs/02-api-reference.md §1 are used, at the least privilege
/// recorded there.
/// </para>
/// </remarks>
public sealed class GraphCapabilityProbe : IGraphCapabilityProbe
{
    /// <summary><c>$select</c> only: <c>/subscribedSkus</c> does not support <c>$filter</c>.</summary>
    private const string SubscribedSkusUri = "v1.0/subscribedSkus?$select=skuId,skuPartNumber";

    private const string DirectorySubscriptionsUri = "v1.0/directory/subscriptions";

    private const string OrganizationUri = "v1.0/organization?$select=displayName,verifiedDomains";

    /// <summary>
    /// The shortest period available, so the probe answers "may we read reports" without
    /// pulling ninety days of CSV.
    /// </summary>
    private const string UsageReportUri = "v1.0/reports/getOffice365ActiveUserDetail(period='D7')";

    private readonly GraphApiClient _client;
    private readonly ILogger<GraphCapabilityProbe> _logger;

    public GraphCapabilityProbe(GraphApiClient client, ILogger<GraphCapabilityProbe> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GraphProbeResult> ProbeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var skus = await _client.CanReadAsync(
            tenantId, MicrosoftProvider.Graph, TokenAudience.Graph, SubscribedSkusUri, cancellationToken)
            .ConfigureAwait(false);

        var subscriptions = await _client.GetJsonOrDefaultAsync<GraphCollection<CompanySubscription>>(
            tenantId, MicrosoftProvider.Graph, TokenAudience.Graph, DirectorySubscriptionsUri, cancellationToken)
            .ConfigureAwait(false);

        var organization = await _client.GetJsonOrDefaultAsync<GraphCollection<OrganizationResource>>(
            tenantId, MicrosoftProvider.Graph, TokenAudience.Graph, OrganizationUri, cancellationToken)
            .ConfigureAwait(false);

        var usage = await _client.CanReadAsync(
            tenantId, MicrosoftProvider.Graph, TokenAudience.Graph, UsageReportUri, cancellationToken)
            .ConfigureAwait(false);

        var ownerTenantId = FindOwnerTenantId(subscriptions);
        var org = organization?.Value is { Count: > 0 } orgs ? orgs[0] : null;

        if (ownerTenantId is not null)
        {
            _logger.LogInformation(
                "Tenant {TenantId} has partner-created subscriptions owned by {OwnerTenantId}; treating as CSP-managed.",
                tenantId,
                ownerTenantId);
        }

        return new GraphProbeResult(
            SubscribedSkusReadable: skus,
            DirectorySubscriptionsReadable: subscriptions is not null,
            UsageReportsReadable: usage,
            OwnerTenantId: ownerTenantId,
            OrganizationDisplayName: org?.DisplayName,
            DefaultDomain: org?.VerifiedDomains?.FirstOrDefault(d => d.IsDefault)?.Name);
    }

    /// <summary>
    /// Returns the partner tenant that created these subscriptions, if any.
    /// </summary>
    /// <remarks>
    /// <c>ownerTenantId</c> being populated on any subscription is the documented signal for
    /// scenario D (docs/01-scope-and-scenarios.md §4). A tenant can hold a mix, so one
    /// partner-created subscription is enough to mean prices must not be promised.
    /// </remarks>
    private static Guid? FindOwnerTenantId(GraphCollection<CompanySubscription>? subscriptions)
    {
        if (subscriptions?.Value is null)
        {
            return null;
        }

        foreach (var subscription in subscriptions.Value)
        {
            if (Guid.TryParse(subscription.OwnerTenantId, out var ownerTenantId) && ownerTenantId != Guid.Empty)
            {
                return ownerTenantId;
            }
        }

        return null;
    }

    private sealed record GraphCollection<T>
    {
        [JsonPropertyName("value")]
        public IReadOnlyList<T>? Value { get; init; }

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed record CompanySubscription
    {
        public string? Id { get; init; }

        public string? SkuPartNumber { get; init; }

        public string? Status { get; init; }

        public bool? IsTrial { get; init; }

        /// <summary>Set when a partner created the subscription. The scenario D signal.</summary>
        public string? OwnerTenantId { get; init; }

        public DateTimeOffset? NextLifecycleDateTime { get; init; }
    }

    private sealed record OrganizationResource
    {
        public string? DisplayName { get; init; }

        public IReadOnlyList<VerifiedDomain>? VerifiedDomains { get; init; }
    }

    private sealed record VerifiedDomain
    {
        public string? Name { get; init; }

        public bool IsDefault { get; init; }
    }
}
