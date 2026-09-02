using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Onboarding;

/// <summary>
/// What a probe of Microsoft Graph found for a tenant.
/// </summary>
/// <remarks>
/// Each flag records whether a specific call <em>succeeded</em>, not whether data existed. A
/// tenant with zero licences and a tenant we are not permitted to ask are different states and
/// must not collapse into the same boolean.
/// </remarks>
/// <param name="SubscribedSkusReadable"><c>GET /subscribedSkus</c> returned 200.</param>
/// <param name="DirectorySubscriptionsReadable"><c>GET /directory/subscriptions</c> returned 200.</param>
/// <param name="UsageReportsReadable">A usage report call returned 200 or 302.</param>
/// <param name="OwnerTenantId">
/// <c>companySubscription.ownerTenantId</c> when populated. Its presence is the signal that a
/// CSP partner created the subscriptions (scenario D, docs/01 §4).
/// </param>
/// <param name="OrganizationDisplayName">From <c>GET /organization</c>.</param>
/// <param name="DefaultDomain">The tenant's verified default domain.</param>
public sealed record GraphProbeResult(
    bool SubscribedSkusReadable,
    bool DirectorySubscriptionsReadable,
    bool UsageReportsReadable,
    Guid? OwnerTenantId = null,
    string? OrganizationDisplayName = null,
    string? DefaultDomain = null)
{
    public static GraphProbeResult NotReachable { get; } = new(false, false, false);

    /// <summary>True when the tenant's licences are sold through a partner.</summary>
    public bool IsPartnerManaged => OwnerTenantId is not null;
}

/// <summary>One Azure subscription as seen by the ARM probe.</summary>
/// <param name="SubscriptionId">The Azure subscription GUID.</param>
/// <param name="DisplayName">Subscription display name.</param>
/// <param name="AgreementType">
/// From <c>billingProperty/default.billingAccountAgreementType</c>. Mixed estates are normal,
/// so this is per subscription rather than per tenant.
/// </param>
/// <param name="IsAzurePlan">
/// False for the classic CSP Azure offer, which exposes no cost data at all until the customer
/// migrates to an Azure Plan (docs/01 §5.8).
/// </param>
public sealed record AzureSubscriptionProbe(
    Guid SubscriptionId,
    string DisplayName,
    AgreementType AgreementType,
    bool IsAzurePlan = true);

/// <param name="Subscriptions">Subscriptions our service principal can see. Empty is meaningful.</param>
/// <param name="CostManagementQueryable">
/// A scoped Cost Management query succeeded. Separate from seeing subscriptions: Reader grants
/// the list, Cost Management Reader grants the query, and customers frequently grant only one.
/// </param>
public sealed record ArmProbeResult(
    IReadOnlyList<AzureSubscriptionProbe> Subscriptions,
    bool CostManagementQueryable)
{
    public static ArmProbeResult NotReachable { get; } = new([], false);

    public bool HasAnySubscription => Subscriptions.Count > 0;

    /// <summary>True when Azure is present but only through the classic CSP offer.</summary>
    public bool IsClassicCspOnly =>
        Subscriptions.Count > 0 && Subscriptions.TrueForAllAzurePlanFalse();
}

internal static class AzureSubscriptionProbeExtensions
{
    internal static bool TrueForAllAzurePlanFalse(this IReadOnlyList<AzureSubscriptionProbe> subscriptions)
    {
        for (var i = 0; i < subscriptions.Count; i++)
        {
            if (subscriptions[i].IsAzurePlan)
            {
                return false;
            }
        }

        return true;
    }
}

/// <param name="Name">The billing account resource name.</param>
/// <param name="AgreementType">MCA, EA, MPA or MOSA.</param>
public sealed record BillingAccountProbe(string Name, AgreementType AgreementType);

/// <param name="Accounts">Billing accounts visible to us. Empty means no billing relationship we can read.</param>
/// <param name="HasBillingRole">
/// A billing role assignment for our principal was found. Listing accounts can succeed through
/// inherited visibility while every useful read still fails, so this is probed separately.
/// </param>
/// <param name="TransactionsReadable">A transactions call returned 200, which is what unlocks real seat prices.</param>
public sealed record BillingProbeResult(
    IReadOnlyList<BillingAccountProbe> Accounts,
    bool HasBillingRole,
    bool TransactionsReadable)
{
    public static BillingProbeResult NotReachable { get; } = new([], false, false);

    public bool HasAnyAccount => Accounts.Count > 0;

    /// <summary>
    /// The agreement that governs the tenant's Microsoft 365 purchases. MCA wins when several
    /// are present because it is the one that exposes prices.
    /// </summary>
    public AgreementType PrimaryAgreementType
    {
        get
        {
            if (Accounts.Count == 0)
            {
                return AgreementType.NotDiscovered;
            }

            foreach (var preferred in new[] { AgreementType.Mca, AgreementType.Ea, AgreementType.Mpa })
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

/// <summary>Probes one Microsoft surface during onboarding and re-discovery.</summary>
/// <remarks>
/// Probes are the one place the platform calls Microsoft from an interactive path
/// (CLAUDE.md rule 5). They must be cheap, read-only, and must return a result rather than
/// throw when the answer is "you are not allowed" — that answer is the product of the probe,
/// not a failure of it.
/// </remarks>
public interface IGraphCapabilityProbe
{
    Task<GraphProbeResult> ProbeAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGraphCapabilityProbe"/>
public interface IAzureCapabilityProbe
{
    Task<ArmProbeResult> ProbeAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGraphCapabilityProbe"/>
public interface IBillingCapabilityProbe
{
    Task<BillingProbeResult> ProbeAsync(Guid tenantId, CancellationToken cancellationToken);
}
