using System.Globalization;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Http;
using Mlcp.Shared.Resilience;

namespace Mlcp.Application.Onboarding;

/// <summary>What a probe needs to know about the tenant it is probing.</summary>
/// <param name="TenantId">The customer tenant.</param>
/// <param name="ConsentCallbackUtc">
/// When an admin last returned from the consent screen, for the service-principal propagation
/// window (ADR-016 rule 3). Null when unknown.
/// </param>
public sealed record ProbeContext(Guid TenantId, DateTimeOffset? ConsentCallbackUtc = null);

/// <summary>
/// The classified answer to one probe call (ADR-016 rule 1). Each sub-call of a probe is captured
/// independently, so one refusal never discards the others.
/// </summary>
/// <param name="Kind"><see cref="MicrosoftFailureKind.None"/> on success.</param>
/// <param name="StatusCode">HTTP status, or null when there was no response.</param>
/// <param name="ErrorCode">AADSTS or API error code, when known. Never a secret.</param>
/// <param name="RetryAfter">Retry hint for transient failures.</param>
public sealed record ProbeCallOutcome(
    MicrosoftFailureKind Kind,
    int? StatusCode = null,
    string? ErrorCode = null,
    TimeSpan? RetryAfter = null)
{
    public static ProbeCallOutcome Success { get; } = new(MicrosoftFailureKind.None, 200);

    /// <summary>The call was not made (for example, a prerequisite call failed).</summary>
    public static ProbeCallOutcome NotAttempted { get; } = new(MicrosoftFailureKind.None) { Attempted = false };

    public static ProbeCallOutcome Denied { get; } = new(MicrosoftFailureKind.CapabilityDenied, 403);

    public static ProbeCallOutcome FloorRemoved { get; } = new(MicrosoftFailureKind.FloorPermissionRemoved, 403);

    public bool Attempted { get; init; } = true;

    public bool Succeeded => Attempted && Kind == MicrosoftFailureKind.None;

    /// <summary>
    /// Transient, a platform-credential failure, or an unexpected answer: nothing can be concluded
    /// about the capability, so the previous verdict is kept (ADR-016).
    /// </summary>
    public bool IsInconclusive => !Attempted
        || Kind is MicrosoftFailureKind.Transient or MicrosoftFailureKind.PlatformCredential or MicrosoftFailureKind.OtherClientError;

    public bool IsDenied => Attempted && Kind == MicrosoftFailureKind.CapabilityDenied;

    public bool RequiresReconsent => Attempted && MicrosoftFailureClassifier.RequiresReconsent(Kind);

    public static ProbeCallOutcome Transient(string? errorCode = null, TimeSpan? retryAfter = null)
        => new(MicrosoftFailureKind.Transient, null, errorCode, retryAfter);

    public static ProbeCallOutcome From(MicrosoftProbeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new ProbeCallOutcome(response.Kind, response.StatusCode, response.ErrorCode, response.RetryAfter);
    }

    /// <summary>A short, secret-free description for capability detail and logs.</summary>
    public string Describe()
    {
        if (!Attempted)
        {
            return "not attempted";
        }

        var parts = new List<string> { Kind.ToString() };

        if (StatusCode is { } status)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"HTTP {status}"));
        }

        if (!string.IsNullOrWhiteSpace(ErrorCode))
        {
            parts.Add(ErrorCode);
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// What a probe of Microsoft Graph found for a tenant.
/// </summary>
/// <remarks>
/// The three booleans record whether a call <em>succeeded</em>, not whether data existed. The
/// optional outcomes carry the ADR-016 classification of each sub-call; when a fixture omits them
/// they are implied from the booleans (a false floor boolean reads as a 403 on a floor call, a
/// false usage boolean as a tier-2 refusal).
/// </remarks>
/// <param name="SubscribedSkusReadable"><c>GET /subscribedSkus</c> returned 200.</param>
/// <param name="DirectorySubscriptionsReadable"><c>GET /directory/subscriptions</c> returned 200.</param>
/// <param name="UsageReportsReadable">A usage report call (Usage Insights app) returned 200 or 302.</param>
/// <param name="OwnerTenantId">
/// <c>companySubscription.ownerTenantId</c> when populated with another tenant: the scenario D signal.
/// </param>
/// <param name="OrganizationDisplayName">From <c>GET /organization</c>.</param>
/// <param name="DefaultDomain">The tenant's verified default domain.</param>
/// <param name="VerifiedDomains">All verified domain names from <c>GET /organization</c>; null if not read.</param>
/// <param name="SubscribedSkus">Classified outcome of the <c>subscribedSkus</c> call.</param>
/// <param name="DirectorySubscriptions">Classified outcome of the <c>directory/subscriptions</c> call.</param>
/// <param name="Organization">Classified outcome of the <c>organization</c> call.</param>
/// <param name="UsageReports">Classified outcome of the usage report call.</param>
public sealed record GraphProbeResult(
    bool SubscribedSkusReadable,
    bool DirectorySubscriptionsReadable,
    bool UsageReportsReadable,
    Guid? OwnerTenantId = null,
    string? OrganizationDisplayName = null,
    string? DefaultDomain = null,
    IReadOnlyList<string>? VerifiedDomains = null,
    ProbeCallOutcome? SubscribedSkus = null,
    ProbeCallOutcome? DirectorySubscriptions = null,
    ProbeCallOutcome? Organization = null,
    ProbeCallOutcome? UsageReports = null)
{
    /// <summary>Nothing readable, as if every floor call had been refused.</summary>
    public static GraphProbeResult NotReachable { get; } = new(false, false, false);

    /// <summary>Every call inconclusive, for a probe that could not run at all.</summary>
    public static GraphProbeResult Inconclusive(ProbeCallOutcome outcome)
        => new(false, false, false, SubscribedSkus: outcome, DirectorySubscriptions: outcome, Organization: outcome, UsageReports: outcome);

    /// <summary>True when the tenant's licences are sold through a partner.</summary>
    public bool IsPartnerManaged => OwnerTenantId is not null;

    public ProbeCallOutcome SkusOutcome => SubscribedSkus ?? (SubscribedSkusReadable ? ProbeCallOutcome.Success : ProbeCallOutcome.FloorRemoved);

    public ProbeCallOutcome DirectoryOutcome => DirectorySubscriptions ?? (DirectorySubscriptionsReadable ? ProbeCallOutcome.Success : ProbeCallOutcome.FloorRemoved);

    public ProbeCallOutcome OrganizationOutcome => Organization ?? ProbeCallOutcome.NotAttempted;

    public ProbeCallOutcome UsageOutcome => UsageReports ?? (UsageReportsReadable ? ProbeCallOutcome.Success : ProbeCallOutcome.Denied);

    /// <summary>
    /// The combined verdict on the universal floor: a re-consent kind if any floor call produced
    /// one; otherwise an inconclusive outcome if any floor call was inconclusive; otherwise success.
    /// </summary>
    public ProbeCallOutcome FloorOutcome
    {
        get
        {
            ProbeCallOutcome[] floor = [SkusOutcome, DirectoryOutcome, OrganizationOutcome];

            foreach (var outcome in floor)
            {
                if (outcome.RequiresReconsent)
                {
                    return outcome;
                }
            }

            foreach (var outcome in floor)
            {
                if (outcome.Attempted && !outcome.Succeeded)
                {
                    return outcome;
                }
            }

            return SkusOutcome.Succeeded ? ProbeCallOutcome.Success : SkusOutcome;
        }
    }
}

/// <summary>One Azure subscription as seen by the ARM probe.</summary>
/// <param name="SubscriptionId">The Azure subscription GUID.</param>
/// <param name="DisplayName">Subscription display name.</param>
/// <param name="AgreementType">
/// From <c>billingProperty/default.billingAccountAgreementType</c>; NotDiscovered when that call
/// was not made or not readable. Mixed estates are normal, so this is per subscription.
/// </param>
/// <param name="IsAzurePlan">
/// False for the classic CSP Azure offer (docs/01 §5.8); true when the billing property shows an
/// Azure plan or a non-partner agreement; null when unknown (CLAUDE.md rule 11 — never guessed).
/// </param>
/// <param name="BillingProfileId">From the billing property, when present.</param>
/// <param name="BillingProperty">Outcome of the billing property call.</param>
public sealed record AzureSubscriptionProbe(
    Guid SubscriptionId,
    string DisplayName,
    AgreementType AgreementType,
    bool? IsAzurePlan = null,
    string? BillingProfileId = null,
    ProbeCallOutcome? BillingProperty = null);

/// <param name="Subscriptions">Subscriptions our service principal can see (possibly a first page). Empty is meaningful.</param>
/// <param name="CostManagementQueryable">A subscription-scope Cost Management query succeeded.</param>
/// <param name="SubscriptionList">Outcome of <c>GET /subscriptions</c>.</param>
/// <param name="SubscriptionCostQuery">Outcome of the subscription-scope cost query.</param>
/// <param name="RootManagementGroupCostQuery">Outcome of the root-management-group cost query (ADR-017 rung 2).</param>
/// <param name="TotalSubscriptionCount">Total visible, when more were listed than classified.</param>
public sealed record ArmProbeResult(
    IReadOnlyList<AzureSubscriptionProbe> Subscriptions,
    bool CostManagementQueryable,
    ProbeCallOutcome? SubscriptionList = null,
    ProbeCallOutcome? SubscriptionCostQuery = null,
    ProbeCallOutcome? RootManagementGroupCostQuery = null,
    int? TotalSubscriptionCount = null)
{
    /// <summary>The list call succeeded and returned nothing.</summary>
    public static ArmProbeResult NotReachable { get; } = new([], false);

    public static ArmProbeResult Inconclusive(ProbeCallOutcome outcome)
        => new([], false, SubscriptionList: outcome, SubscriptionCostQuery: outcome, RootManagementGroupCostQuery: outcome);

    public bool HasAnySubscription => Subscriptions.Count > 0;

    public int SubscriptionCount => TotalSubscriptionCount ?? Subscriptions.Count;

    public ProbeCallOutcome SubscriptionListOutcome => SubscriptionList ?? ProbeCallOutcome.Success;

    public ProbeCallOutcome CostQueryOutcome => SubscriptionCostQuery
        ?? (CostManagementQueryable ? ProbeCallOutcome.Success : HasAnySubscription ? ProbeCallOutcome.Denied : ProbeCallOutcome.NotAttempted);

    public ProbeCallOutcome RootManagementGroupOutcome => RootManagementGroupCostQuery ?? ProbeCallOutcome.NotAttempted;

    /// <summary>True when Azure is present and every classified subscription is known to be classic CSP.</summary>
    public bool IsClassicCspOnly
    {
        get
        {
            var classified = Subscriptions.Where(s => s.IsAzurePlan is not null).ToList();
            return classified.Count > 0
                && classified.Count == Subscriptions.Count
                && classified.TrueForAll(s => s.IsAzurePlan == false);
        }
    }
}

/// <param name="Name">The billing account resource name.</param>
/// <param name="AgreementType">MCA, EA, MPA or MOSA (Unknown for unrecognised values).</param>
public sealed record BillingAccountProbe(string Name, AgreementType AgreementType);

/// <param name="Accounts">Billing accounts visible to us. Empty without a billing role — not evidence of anything.</param>
/// <param name="HasBillingRole">A billing role assignment read on the primary account succeeded.</param>
/// <param name="TransactionsReadable">A billing-profile transactions call returned 200.</param>
/// <param name="AccountList">Outcome of the billing account list.</param>
/// <param name="RoleAssignments">Outcome of the billing role assignment read.</param>
/// <param name="Transactions">Outcome of the transactions read.</param>
/// <param name="BillingProfileIds">Fully qualified billing profile ids read on the primary MCA account.</param>
/// <param name="PrimaryAccountName">The account the role and transaction probes ran against.</param>
/// <param name="BillingScopeCostQuery">Outcome of a Cost Management query at the first billing profile (MCA) or the EA account (ADR-017 rung 1).</param>
public sealed record BillingProbeResult(
    IReadOnlyList<BillingAccountProbe> Accounts,
    bool HasBillingRole,
    bool TransactionsReadable,
    ProbeCallOutcome? AccountList = null,
    ProbeCallOutcome? RoleAssignments = null,
    ProbeCallOutcome? Transactions = null,
    IReadOnlyList<string>? BillingProfileIds = null,
    string? PrimaryAccountName = null,
    ProbeCallOutcome? BillingScopeCostQuery = null)
{
    public static BillingProbeResult NotReachable { get; } = new([], false, false);

    public static BillingProbeResult Inconclusive(ProbeCallOutcome outcome)
        => new([], false, false, AccountList: outcome, RoleAssignments: outcome, Transactions: outcome, BillingScopeCostQuery: outcome);

    public bool HasAnyAccount => Accounts.Count > 0;

    public ProbeCallOutcome AccountListOutcome => AccountList ?? ProbeCallOutcome.Success;

    public ProbeCallOutcome RoleOutcome => RoleAssignments
        ?? (HasBillingRole ? ProbeCallOutcome.Success : HasAnyAccount ? ProbeCallOutcome.Denied : ProbeCallOutcome.NotAttempted);

    public ProbeCallOutcome TransactionsOutcome => Transactions
        ?? (TransactionsReadable ? ProbeCallOutcome.Success : HasBillingRole ? ProbeCallOutcome.Denied : ProbeCallOutcome.NotAttempted);

    public ProbeCallOutcome BillingScopeCostOutcome => BillingScopeCostQuery ?? ProbeCallOutcome.NotAttempted;

    /// <summary>
    /// The agreement that governs the tenant's purchases, from visible accounts. MCA wins when
    /// several are present because it is the one that exposes prices.
    /// </summary>
    public AgreementType PrimaryAgreementType
    {
        get
        {
            if (Accounts.Count == 0)
            {
                return AgreementType.NotDiscovered;
            }

            foreach (var preferred in new[] { AgreementType.Mca, AgreementType.Ea, AgreementType.Mpa, AgreementType.Mosa })
            {
                foreach (var account in Accounts)
                {
                    if (account.AgreementType == preferred)
                    {
                        return preferred;
                    }
                }
            }

            return Accounts[0].AgreementType;
        }
    }
}

/// <summary>Probes Microsoft Graph during onboarding and re-discovery.</summary>
/// <remarks>
/// Probes must be cheap and read-only, and must return a result rather than throw when the answer
/// is "you are not allowed" — that answer is the product of the probe (ADR-016 rule 1). They never
/// change a tenant.
/// </remarks>
public interface IGraphCapabilityProbe
{
    Task<GraphProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken);

    /// <summary>
    /// The floor calls only (<c>subscribedSkus</c>, <c>directory/subscriptions</c>,
    /// <c>organization</c>), for the automatic re-consent probe (ADR-016 rule 5).
    /// </summary>
    Task<ProbeCallOutcome> ProbeFloorAsync(ProbeContext context, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGraphCapabilityProbe"/>
public interface IAzureCapabilityProbe
{
    Task<ArmProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGraphCapabilityProbe"/>
public interface IBillingCapabilityProbe
{
    Task<BillingProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken);
}
