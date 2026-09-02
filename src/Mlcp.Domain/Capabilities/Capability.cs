namespace Mlcp.Domain.Capabilities;

/// <summary>
/// A distinct Microsoft data source the platform can read from. Capability is tracked per
/// data source rather than per tenant because mixed estates are common — for example MCA for
/// Microsoft 365 alongside CSP-managed Azure (docs/01-scope-and-scenarios.md §4).
/// </summary>
public enum Capability
{
    Unknown = 0,

    /// <summary>Graph licensing and directory. The universal floor; available to every tenant.</summary>
    GraphLicensing = 1,

    /// <summary>Graph usage reports. Requires the tier-2 <c>Reports.Read.All</c> grant.</summary>
    GraphUsage = 2,

    /// <summary>Azure Resource Manager reachable at all: at least one subscription is visible.</summary>
    ArmAccess = 3,

    /// <summary>Cost Management query and forecast. Requires Azure RBAC Cost Management Reader.</summary>
    CostManagement = 4,

    /// <summary>Billing account hierarchy, subscriptions, terms and auto-renew. Requires a billing role.</summary>
    BillingAccount = 5,

    /// <summary>Transactions and invoices, and therefore real seat prices. Requires a billing role.</summary>
    BillingTransactions = 6,

    /// <summary>Partner Center. Phase 6; partner tenants only.</summary>
    PartnerCenter = 7,
}
