using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Integration.Azure;

/// <summary>
/// Probes <c>Microsoft.Billing</c> for the billing account hierarchy, our role on it, and
/// whether transactions — the source of real seat prices — can be read.
/// </summary>
/// <remarks>
/// <para>
/// Three separate questions, because they fail independently and the onboarding checklist has
/// to name which one failed. Listing billing accounts can succeed while every useful read is
/// refused, so a successful list is not evidence of a usable grant (docs/03 §4.1).
/// </para>
/// <para>
/// Billing roles are a third authorisation system, distinct from Entra consent and from Azure
/// RBAC. A customer can complete both of the other two and still see no prices.
/// </para>
/// </remarks>
public sealed class BillingCapabilityProbe : IBillingCapabilityProbe
{
    private const string BillingAccountsUri =
        "providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01";

    private static string RoleAssignmentsUri(string accountName) => string.Create(
        CultureInfo.InvariantCulture,
        $"providers/Microsoft.Billing/billingAccounts/{accountName}/billingRoleAssignments?api-version=2024-04-01");

    private static string BillingProfilesUri(string accountName) => string.Create(
        CultureInfo.InvariantCulture,
        $"providers/Microsoft.Billing/billingAccounts/{accountName}/billingProfiles?api-version=2024-04-01");

    private static string TransactionsUri(string accountName, string profileName, DateTimeOffset from, DateTimeOffset to)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"providers/Microsoft.Billing/billingAccounts/{accountName}/billingProfiles/{profileName}/transactions"
            + $"?api-version=2024-04-01&startDate={from:yyyy-MM-dd}&endDate={to:yyyy-MM-dd}&type=unbilled");

    private readonly AzureApiClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BillingCapabilityProbe> _logger;

    public BillingCapabilityProbe(
        AzureApiClient client,
        TimeProvider timeProvider,
        ILogger<BillingCapabilityProbe> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BillingProbeResult> ProbeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var listing = await _client.GetJsonOrDefaultAsync<ArmCollection<BillingAccountResource>>(
            tenantId, MicrosoftProvider.AzureBilling, TokenAudience.ResourceManager, BillingAccountsUri, cancellationToken)
            .ConfigureAwait(false);

        if (listing?.Value is not { Count: > 0 } accounts)
        {
            return BillingProbeResult.NotReachable;
        }

        var probes = new List<BillingAccountProbe>(accounts.Count);

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Name))
            {
                continue;
            }

            probes.Add(new BillingAccountProbe(
                account.Name,
                AzureCapabilityProbe.MapAgreementType(account.Properties?.AgreementType)));
        }

        if (probes.Count == 0)
        {
            return BillingProbeResult.NotReachable;
        }

        var primary = probes[0].Name;

        var hasRole = await HasBillingRoleAsync(tenantId, primary, cancellationToken).ConfigureAwait(false);

        var transactionsReadable = hasRole
            && await CanReadTransactionsAsync(tenantId, primary, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Billing probe for tenant {TenantId}: {AccountCount} account(s), role {HasRole}, transactions {TransactionsReadable}.",
            tenantId,
            probes.Count,
            hasRole,
            transactionsReadable);

        return new BillingProbeResult(probes, hasRole, transactionsReadable);
    }

    /// <summary>
    /// Whether any billing role assignment is visible on the account.
    /// </summary>
    /// <remarks>
    /// The endpoint itself requires a billing role to read, so a successful response is the
    /// evidence — the assignments it returns are used later to tell the customer which role is
    /// missing at which scope (docs/05 P3-6).
    /// </remarks>
    private async Task<bool> HasBillingRoleAsync(Guid tenantId, string accountName, CancellationToken cancellationToken)
    {
        var uri = RoleAssignmentsUri(accountName);

        return await _client.CanReadAsync(
            tenantId, MicrosoftProvider.AzureBilling, TokenAudience.ResourceManager, uri, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether transactions can be read, which is what actually unlocks seat prices.
    /// </summary>
    /// <remarks>
    /// Transactions hang off a billing profile, not the account, so this resolves a profile
    /// first. An account with no readable profile is a real state: it means a role exists at
    /// the account but not at the scope where prices live.
    /// </remarks>
    private async Task<bool> CanReadTransactionsAsync(Guid tenantId, string accountName, CancellationToken cancellationToken)
    {
        var profilesUri = BillingProfilesUri(accountName);

        var profiles = await _client.GetJsonOrDefaultAsync<ArmCollection<BillingProfileResource>>(
            tenantId, MicrosoftProvider.AzureBilling, TokenAudience.ResourceManager, profilesUri, cancellationToken)
            .ConfigureAwait(false);

        if (profiles?.Value is not { Count: > 0 } profileList || string.IsNullOrWhiteSpace(profileList[0].Name))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();

        var uri = TransactionsUri(accountName, profileList[0].Name!, now.AddDays(-30), now);

        return await _client.CanReadAsync(
            tenantId, MicrosoftProvider.AzureBilling, TokenAudience.ResourceManager, uri, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record ArmCollection<T>
    {
        [JsonPropertyName("value")]
        public IReadOnlyList<T>? Value { get; init; }

        [JsonPropertyName("nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed record BillingAccountResource
    {
        public string? Name { get; init; }

        public BillingAccountProperties? Properties { get; init; }
    }

    private sealed record BillingAccountProperties
    {
        public string? DisplayName { get; init; }

        public string? AgreementType { get; init; }

        public string? AccountType { get; init; }
    }

    private sealed record BillingProfileResource
    {
        public string? Name { get; init; }

        public BillingProfileProperties? Properties { get; init; }
    }

    private sealed record BillingProfileProperties
    {
        public string? DisplayName { get; init; }

        public string? Currency { get; init; }

        public string? Status { get; init; }
    }
}
