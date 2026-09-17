using System.Globalization;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Costs;

/// <summary>What is known about cost access on one subscription.</summary>
/// <param name="SubscriptionId">The Azure subscription id.</param>
/// <param name="AgreementType">From the subscription's billing property.</param>
/// <param name="CostReadable">True when a cost query on it succeeded; false when refused; null when not tried.</param>
public sealed record SubscriptionCostAccess(Guid SubscriptionId, AgreementType AgreementType, bool? CostReadable);

/// <summary>Inputs to the scope ladder for one tenant.</summary>
/// <param name="TenantId">The tenant; its id is also the root management group id.</param>
/// <param name="AgreementType">The resolved primary agreement.</param>
/// <param name="HasBillingRole">A billing role read succeeded (tier 4).</param>
/// <param name="BillingScopeCostReadable">A cost query at the first billing scope (profile or EA account) succeeded.</param>
/// <param name="BillingProfileIds">Fully qualified MCA billing profile ids readable with that role.</param>
/// <param name="EaBillingAccountId">Fully qualified EA billing account id, when the tenant is EA and it is visible.</param>
/// <param name="RootManagementGroupCostReadable">A cost query at the root management group succeeded.</param>
/// <param name="Subscriptions">Visible subscriptions.</param>
public sealed record CostScopeInput(
    Guid TenantId,
    AgreementType AgreementType,
    bool HasBillingRole,
    bool BillingScopeCostReadable,
    IReadOnlyList<string> BillingProfileIds,
    string? EaBillingAccountId,
    bool RootManagementGroupCostReadable,
    IReadOnlyList<SubscriptionCostAccess> Subscriptions);

/// <summary>The chosen rung and the scopes a cost sync should query, one query per scope.</summary>
public sealed record CostScopePlan(CostQueryScopeRung Rung, IReadOnlyList<string> Scopes, string Reason)
{
    /// <summary>Reservations and Marketplace purchases are only visible at billing scope (ADR-017).</summary>
    public bool PurchasesVisible => Rung == CostQueryScopeRung.BillingScope;

    /// <summary>Large rung-3 tenants are nudged towards a billing role or Exports.</summary>
    public bool RecommendBillingRoleOrExports
        => Rung == CostQueryScopeRung.PerSubscription && Scopes.Count > CostScopeResolver.PerSubscriptionRecommendationThreshold;

    public static CostScopePlan None(string reason) => new(CostQueryScopeRung.None, [], reason);
}

/// <summary>
/// Picks the widest Cost Management scope the app can read, per tenant (ADR-017):
/// rung 1 billing scope → rung 2 root management group → rung 3 per subscription.
/// </summary>
/// <remarks>
/// Pure. Recomputed on each capability discovery and recorded in the capability profile; the
/// Phase 4 cost sync uses the same function. Management groups are never used for MCA or CSP
/// subscriptions, which Cost Management does not support there
/// (<see href="https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/understand-work-scopes">Understand and work with scopes</see>).
/// </remarks>
public static class CostScopeResolver
{
    /// <summary>Above this many subscriptions on rung 3 the detail recommends a billing role or Exports.</summary>
    public const int PerSubscriptionRecommendationThreshold = 300;

    public static string RootManagementGroupScope(Guid tenantId)
        => string.Create(CultureInfo.InvariantCulture, $"/providers/Microsoft.Management/managementGroups/{tenantId:D}");

    public static string SubscriptionScope(Guid subscriptionId)
        => string.Create(CultureInfo.InvariantCulture, $"/subscriptions/{subscriptionId:D}");

    public static CostScopePlan Resolve(CostScopeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Rung 1: billing scope, which is the only one that includes purchases.
        if (input.HasBillingRole && input.BillingScopeCostReadable)
        {
            if (input.AgreementType == AgreementType.Mca && input.BillingProfileIds.Count > 0)
            {
                return new CostScopePlan(
                    CostQueryScopeRung.BillingScope,
                    input.BillingProfileIds,
                    "MCA with a billing role: one query per billing profile.");
            }

            if (input.AgreementType == AgreementType.Ea && !string.IsNullOrWhiteSpace(input.EaBillingAccountId))
            {
                return new CostScopePlan(
                    CostQueryScopeRung.BillingScope,
                    [input.EaBillingAccountId],
                    "Enterprise Agreement with enterprise read access: one query at the billing account.");
            }
        }

        // Rung 2: root management group, for EA and MOSP only, and only if no subscription is on
        // an agreement Cost Management does not support at management-group scope.
        var mgCompatible = input.AgreementType is AgreementType.Ea or AgreementType.Mosa
            && input.Subscriptions.All(s => s.AgreementType is not (AgreementType.Mca or AgreementType.Mpa or AgreementType.CspManaged));

        if (mgCompatible && input.RootManagementGroupCostReadable)
        {
            return new CostScopePlan(
                CostQueryScopeRung.RootManagementGroup,
                [RootManagementGroupScope(input.TenantId)],
                "Cost Management Reader at the root management group: one query, usage only (purchases need billing access).");
        }

        // Rung 3: per subscription, only where rungs 1-2 are unavailable, and only if at least one
        // subscription answered a cost query. Untried subscriptions are included; the sync
        // verifies each and records refusals.
        var anyReadable = input.Subscriptions.Any(s => s.CostReadable == true);
        var candidates = input.Subscriptions
            .Where(s => s.CostReadable != false)
            .Select(s => SubscriptionScope(s.SubscriptionId))
            .ToList();

        if (anyReadable && candidates.Count > 0)
        {
            var reason = candidates.Count > PerSubscriptionRecommendationThreshold
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Per-subscription queries over {candidates.Count} subscriptions under the QPU budget. Grant a billing role or configure Exports for faster, complete cost data.")
                : "Per-subscription queries under the QPU budget; purchases need billing access.";

            return new CostScopePlan(CostQueryScopeRung.PerSubscription, candidates, reason);
        }

        return CostScopePlan.None(input.Subscriptions.Count == 0
            ? "No Azure subscriptions are visible and no billing scope is readable."
            : "No Cost Management scope is readable: assign Cost Management Reader, or grant a billing role.");
    }
}
