using Mlcp.Domain.Tenancy;

namespace Mlcp.Domain.Capabilities;

/// <summary>
/// The rung of the Cost Management scope ladder chosen for a tenant (ADR-017).
/// </summary>
public enum CostQueryScopeRung
{
    /// <summary>No scope is readable: Cost Management is unavailable.</summary>
    None = 0,

    /// <summary>Rung 1: MCA billing profile(s) or the EA billing account. Includes purchases.</summary>
    BillingScope = 1,

    /// <summary>Rung 2: the root management group (EA / MOSP only). Usage only, no purchases.</summary>
    RootManagementGroup = 2,

    /// <summary>Rung 3: one query per subscription, under the QPU budget. Usage only, no purchases.</summary>
    PerSubscription = 3,
}

/// <summary>What discovery learned about one Azure subscription's agreement (ADR-017, ADR-020).</summary>
/// <param name="SubscriptionId">The Azure subscription id.</param>
/// <param name="AgreementType">From <c>billingProperty.billingAccountAgreementType</c>; NotDiscovered if unreadable.</param>
/// <param name="IsAzurePlan">False for classic CSP; null when unknown (rule 11: never guessed).</param>
public sealed record SubscriptionAgreement(Guid SubscriptionId, AgreementType AgreementType, bool? IsAzurePlan);

/// <summary>
/// Discovery facts that are not a capability verdict but that later phases and the checklist need:
/// the cost scope rung and the per-subscription agreement map. Stored as JSON on
/// <see cref="TenantCapabilityProfile"/>.
/// </summary>
public sealed record CapabilityDiscoveryDetail
{
    public static CapabilityDiscoveryDetail Empty { get; } = new();

    public CostQueryScopeRung CostScopeRung { get; init; }

    /// <summary>Why the rung was chosen, for the capability detail and the sync log.</summary>
    public string? CostScopeReason { get; init; }

    /// <summary>How many scopes the rung queries (profiles, 1, or subscriptions).</summary>
    public int CostScopeCount { get; init; }

    /// <summary>False on rungs 2 and 3: reservations and Marketplace purchases are not visible.</summary>
    public bool PurchasesVisible { get; init; }

    /// <summary>How many subscriptions were visible in total (only a sample is classified).</summary>
    public int VisibleSubscriptionCount { get; init; }

    public IReadOnlyList<SubscriptionAgreement> Subscriptions { get; init; } = [];
}
