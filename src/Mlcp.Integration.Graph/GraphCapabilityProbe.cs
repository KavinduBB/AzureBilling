using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Http;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Integration.Graph;

/// <summary>
/// Probes Microsoft Graph for the universal floor, partner management, directory details and the
/// tier-2 usage grant.
/// </summary>
/// <remarks>
/// <para>
/// Every call goes through the non-throwing <see cref="MicrosoftApiClient.ProbeAsync{T}"/> and is
/// captured independently (ADR-016 rule 1): a refused usage report never hides a readable floor.
/// The three floor calls are marked as such, so a 403 on them is classified
/// FloorPermissionRemoved and a 401 GrantRevoked; the usage call uses the Usage Insights app
/// (ADR-015), whose refusals are only ever CapabilityDenied.
/// </para>
/// <para>
/// Only app-only endpoints listed in docs/02 §1 are used. <c>/users/{id}/licenseDetails</c> is
/// deliberately absent: it needs a user id and is not a probe.
/// </para>
/// </remarks>
public sealed class GraphCapabilityProbe : IGraphCapabilityProbe
{
    /// <summary><c>$select</c> only: <c>/subscribedSkus</c> does not support <c>$filter</c>.</summary>
    internal const string SubscribedSkusUri = "v1.0/subscribedSkus?$select=skuId,skuPartNumber";

    internal const string DirectorySubscriptionsUri = "v1.0/directory/subscriptions";

    internal const string OrganizationUri = "v1.0/organization?$select=id,displayName,verifiedDomains";

    /// <summary>The shortest period, so the probe answers "may we read reports" without pulling 90 days of CSV.</summary>
    internal const string UsageReportUri = "v1.0/reports/getOffice365ActiveUserDetail(period='D7')";

    private readonly GraphApiClient _client;
    private readonly ILogger<GraphCapabilityProbe> _logger;

    public GraphCapabilityProbe(GraphApiClient client, ILogger<GraphCapabilityProbe> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GraphProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var floor = await ProbeFloorCallsAsync(context, cancellationToken).ConfigureAwait(false);

        var usage = await _client.ProbeAsync(
            new MicrosoftRequest(context.TenantId, MicrosoftProvider.Graph, TokenAudience.GraphReports, UsageReportUri)
            {
                ConsentCallbackUtc = context.ConsentCallbackUtc,
            },
            cancellationToken).ConfigureAwait(false);

        return floor with
        {
            UsageReportsReadable = usage.Succeeded,
            UsageReports = ProbeCallOutcome.From(usage),
        };
    }

    public async Task<ProbeCallOutcome> ProbeFloorAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var floor = await ProbeFloorCallsAsync(context, cancellationToken).ConfigureAwait(false);
        return floor.FloorOutcome;
    }

    private async Task<GraphProbeResult> ProbeFloorCallsAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        var skus = await _client.ProbeAsync(FloorRequest(context, SubscribedSkusUri), cancellationToken).ConfigureAwait(false);

        var subscriptions = await _client
            .ProbeAsync<GraphCollection<CompanySubscription>>(FloorRequest(context, DirectorySubscriptionsUri), cancellationToken)
            .ConfigureAwait(false);

        var organization = await _client
            .ProbeAsync<GraphCollection<OrganizationResource>>(FloorRequest(context, OrganizationUri), cancellationToken)
            .ConfigureAwait(false);

        var ownerTenantId = FindOwnerTenantId(context.TenantId, subscriptions.Value);
        var org = organization.Value?.Value is { Count: > 0 } orgs ? orgs[0] : null;

        if (ownerTenantId is not null)
        {
            _logger.LogInformation(
                "Tenant {TenantId} has partner-created subscriptions owned by {OwnerTenantId}; treating as CSP-managed.",
                context.TenantId,
                ownerTenantId);
        }

        return new GraphProbeResult(
            SubscribedSkusReadable: skus.Succeeded,
            DirectorySubscriptionsReadable: subscriptions.Succeeded,
            UsageReportsReadable: false,
            OwnerTenantId: ownerTenantId,
            OrganizationDisplayName: org?.DisplayName,
            DefaultDomain: org?.VerifiedDomains?.FirstOrDefault(d => d.IsDefault)?.Name,
            VerifiedDomains: org?.VerifiedDomains?
                .Select(d => d.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList(),
            SubscribedSkus: ProbeCallOutcome.From(skus),
            DirectorySubscriptions: ProbeCallOutcome.From(subscriptions),
            Organization: ProbeCallOutcome.From(organization),
            UsageReports: ProbeCallOutcome.NotAttempted);
    }

    private static MicrosoftRequest FloorRequest(ProbeContext context, string uri)
        => new(context.TenantId, MicrosoftProvider.Graph, TokenAudience.Graph, uri)
        {
            IsFloorCall = true,
            ConsentCallbackUtc = context.ConsentCallbackUtc,
        };

    /// <summary>
    /// Returns the partner tenant that created these subscriptions, if any.
    /// </summary>
    /// <remarks>
    /// <c>ownerTenantId</c> set to <em>another</em> tenant is the scenario D signal (docs/01 §4,
    /// ADR-020). A tenant can hold a mix, so one partner-created subscription is enough.
    /// </remarks>
    internal static Guid? FindOwnerTenantId(Guid tenantId, GraphCollection<CompanySubscription>? subscriptions)
    {
        if (subscriptions?.Value is null)
        {
            return null;
        }

        foreach (var subscription in subscriptions.Value)
        {
            if (Guid.TryParse(subscription.OwnerTenantId, out var owner) && owner != Guid.Empty && owner != tenantId)
            {
                return owner;
            }
        }

        return null;
    }

    internal sealed record GraphCollection<T>
    {
        [JsonPropertyName("value")]
        public IReadOnlyList<T>? Value { get; init; }

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }

    internal sealed record CompanySubscription
    {
        public string? Id { get; init; }

        public string? SkuPartNumber { get; init; }

        public string? Status { get; init; }

        public bool? IsTrial { get; init; }

        /// <summary>Set when a partner created the subscription. The scenario D signal.</summary>
        public string? OwnerTenantId { get; init; }

        public DateTimeOffset? NextLifecycleDateTime { get; init; }
    }

    internal sealed record OrganizationResource
    {
        public string? Id { get; init; }

        public string? DisplayName { get; init; }

        public IReadOnlyList<VerifiedDomain>? VerifiedDomains { get; init; }
    }

    internal sealed record VerifiedDomain
    {
        public string? Name { get; init; }

        public bool IsDefault { get; init; }
    }
}
