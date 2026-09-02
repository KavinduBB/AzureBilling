namespace Mlcp.Domain.Tenancy;

/// <summary>
/// Per-tenant feature flags. Serialised to the <c>Features</c> JSON column so adding a flag
/// does not require a migration. All flags default to off: a capability is never assumed.
/// </summary>
public sealed record TenantFeatures
{
    public static TenantFeatures Default { get; } = new();

    /// <summary>Tier-2 usage reporting has been consented and is in use.</summary>
    public bool UsageInsights { get; init; }

    /// <summary>
    /// The customer's admin has turned off report anonymisation in Microsoft 365 and told us so.
    /// Gates per-user and per-department usage views (docs/02-api-reference.md §1.2).
    /// </summary>
    public bool IdentifiableNames { get; init; }

    /// <summary>Phase 5 write operations are enabled for this tenant.</summary>
    public bool LifecycleOps { get; init; }
}
