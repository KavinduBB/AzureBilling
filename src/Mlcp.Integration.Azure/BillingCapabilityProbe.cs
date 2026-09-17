using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Http;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Integration.Azure;

/// <summary>
/// Probes <c>Microsoft.Billing</c> (api-version 2024-04-01) for the billing account hierarchy, our
/// role on it, transactions (real seat prices) and billing-scope cost access (ADR-017 rung 1).
/// </summary>
/// <remarks>
/// <para>
/// Each question is a separate call with its own classified outcome (ADR-016 rule 1). A 200 with
/// no accounts is <em>not</em> evidence of anything: without a billing role the list is simply
/// empty (ADR-020).
/// </para>
/// <para>
/// Role assignments are read only for MCA, EA and MPA accounts, the agreement types that operation
/// supports
/// (<see href="https://learn.microsoft.com/en-us/rest/api/billing/billing-role-assignments/list-by-billing-account?view=rest-billing-2024-04-01">List By Billing Account</see>).
/// Transactions are read at the first MCA/MPA billing profile with the required
/// <c>periodStartDate</c>, <c>periodEndDate</c> and <c>type=Unbilled</c>
/// (<see href="https://learn.microsoft.com/en-us/rest/api/billing/transactions/list-by-billing-profile?view=rest-billing-2024-04-01">List By Billing Profile</see>).
/// </para>
/// </remarks>
public sealed class BillingCapabilityProbe : IBillingCapabilityProbe
{
    internal const string ApiVersion = "2024-04-01";

    internal const string BillingAccountsUri = "providers/Microsoft.Billing/billingAccounts?api-version=" + ApiVersion;

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

    internal static string AccountPath(string accountName)
        => $"providers/Microsoft.Billing/billingAccounts/{accountName}";

    internal static string RoleAssignmentsUri(string accountName)
        => $"{AccountPath(accountName)}/billingRoleAssignments?api-version={ApiVersion}";

    internal static string BillingProfilesUri(string accountName)
        => $"{AccountPath(accountName)}/billingProfiles?api-version={ApiVersion}";

    /// <summary>
    /// Unbilled transactions for the current month so far. All three query parameters are
    /// required by 2024-04-01; <c>top=1</c> keeps the probe to one row.
    /// </summary>
    internal static string TransactionsUri(string profileId, DateOnly from, DateOnly to)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{profileId.TrimStart('/')}/transactions?api-version={ApiVersion}&periodStartDate={from:yyyy-MM-dd}&periodEndDate={to:yyyy-MM-dd}&type=Unbilled&top=1");

    public async Task<BillingProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listing = await _client.ProbeAsync<ArmCollection<BillingAccountResource>>(
            Get(context, BillingAccountsUri), cancellationToken).ConfigureAwait(false);

        var listOutcome = ProbeCallOutcome.From(listing);

        if (!listing.Succeeded)
        {
            return Result([], listOutcome);
        }

        var accounts = (listing.Value?.Value ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new BillingAccountProbe(a.Name!, AzureCapabilityProbe.MapAgreementType(a.Properties?.AgreementType)))
            .ToList();

        var primary = accounts.FirstOrDefault(a => a.AgreementType == AgreementType.Mca)
            ?? accounts.FirstOrDefault(a => a.AgreementType == AgreementType.Ea)
            ?? accounts.FirstOrDefault(a => a.AgreementType == AgreementType.Mpa);

        if (primary is null)
        {
            return Result(accounts, listOutcome);
        }

        var role = ProbeCallOutcome.From(await _client.ProbeAsync(
            Get(context, RoleAssignmentsUri(primary.Name)), cancellationToken).ConfigureAwait(false));

        if (!role.Succeeded)
        {
            return Result(accounts, listOutcome, primary.Name, role);
        }

        var profileIds = new List<string>();
        var transactions = ProbeCallOutcome.NotAttempted;
        ProbeCallOutcome costQuery;

        if (primary.AgreementType == AgreementType.Ea)
        {
            costQuery = ProbeCallOutcome.From(await _client.ProbeAsync(
                CostQueryProbe.Request(context.TenantId, AccountPath(primary.Name), context.ConsentCallbackUtc),
                cancellationToken).ConfigureAwait(false));
        }
        else
        {
            var profiles = await _client.ProbeAsync<ArmCollection<BillingProfileResource>>(
                Get(context, BillingProfilesUri(primary.Name)), cancellationToken).ConfigureAwait(false);

            if (profiles.Succeeded)
            {
                profileIds.AddRange((profiles.Value?.Value ?? [])
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                    .Select(p => string.IsNullOrWhiteSpace(p.Id) ? $"/{AccountPath(primary.Name)}/billingProfiles/{p.Name}" : p.Id!));
            }

            if (profileIds.Count > 0)
            {
                var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
                var monthStart = new DateOnly(today.Year, today.Month, 1);

                transactions = ProbeCallOutcome.From(await _client.ProbeAsync(
                    Get(context, TransactionsUri(profileIds[0], monthStart, today)), cancellationToken).ConfigureAwait(false));

                costQuery = ProbeCallOutcome.From(await _client.ProbeAsync(
                    CostQueryProbe.Request(context.TenantId, profileIds[0], context.ConsentCallbackUtc),
                    cancellationToken).ConfigureAwait(false));
            }
            else
            {
                transactions = profiles.Succeeded ? ProbeCallOutcome.NotAttempted : ProbeCallOutcome.From(profiles);
                costQuery = ProbeCallOutcome.NotAttempted;
            }
        }

        _logger.LogInformation(
            "Billing probe for tenant {TenantId}: {AccountCount} account(s), primary {Agreement}, role {Role}, transactions {Transactions}, billing-scope cost {CostQuery}.",
            context.TenantId,
            accounts.Count,
            primary.AgreementType,
            role.Describe(),
            transactions.Describe(),
            costQuery.Describe());

        return new BillingProbeResult(
            accounts,
            HasBillingRole: true,
            TransactionsReadable: transactions.Succeeded,
            AccountList: listOutcome,
            RoleAssignments: role,
            Transactions: transactions,
            BillingProfileIds: profileIds,
            PrimaryAccountName: primary.Name,
            BillingScopeCostQuery: costQuery);
    }

    private static BillingProbeResult Result(
        IReadOnlyList<BillingAccountProbe> accounts,
        ProbeCallOutcome list,
        string? primary = null,
        ProbeCallOutcome? role = null)
        => new(
            accounts,
            HasBillingRole: false,
            TransactionsReadable: false,
            AccountList: list,
            RoleAssignments: role ?? ProbeCallOutcome.NotAttempted,
            Transactions: ProbeCallOutcome.NotAttempted,
            BillingProfileIds: [],
            PrimaryAccountName: primary,
            BillingScopeCostQuery: ProbeCallOutcome.NotAttempted);

    private static MicrosoftRequest Get(ProbeContext context, string uri)
        => new(context.TenantId, MicrosoftProvider.AzureBilling, TokenAudience.ResourceManager, uri)
        {
            ConsentCallbackUtc = context.ConsentCallbackUtc,
        };

    internal sealed record ArmCollection<T>
    {
        [JsonPropertyName("value")]
        public IReadOnlyList<T>? Value { get; init; }

        [JsonPropertyName("nextLink")]
        public string? NextLink { get; init; }
    }

    internal sealed record BillingAccountResource
    {
        public string? Name { get; init; }

        public BillingAccountProperties? Properties { get; init; }
    }

    internal sealed record BillingAccountProperties
    {
        public string? DisplayName { get; init; }

        public string? AgreementType { get; init; }

        public string? AccountType { get; init; }
    }

    internal sealed record BillingProfileResource
    {
        public string? Id { get; init; }

        public string? Name { get; init; }
    }
}
