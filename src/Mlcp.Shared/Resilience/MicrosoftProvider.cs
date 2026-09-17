namespace Mlcp.Shared.Resilience;

/// <summary>
/// A Microsoft API surface with its own throttling behaviour and its own failure domain.
/// Circuit breakers are keyed per (tenant, provider); concurrency limits per provider, so that,
/// for example, Cost Management being throttled does not stop licence data from syncing.
/// </summary>
public enum MicrosoftProvider
{
    Unknown = 0,
    Graph = 1,
    AzureBilling = 2,
    CostManagement = 3,
    Consumption = 4,
    Advisor = 5,
    ResourceManager = 6,
    PartnerCenter = 7,
}

/// <summary>
/// Circuit-breaker identity: one breaker per tenant per provider.
/// </summary>
/// <remarks>
/// Keying by tenant as well as provider matters in a multi-tenant platform. One customer whose
/// consent has lapsed, or whose billing scope is misconfigured, would otherwise trip a shared
/// breaker and stop every other customer's sync for the same provider.
/// </remarks>
/// <param name="TenantId">The customer tenant the calls are made for.</param>
/// <param name="Provider">The Microsoft API surface being called.</param>
public readonly record struct ProviderCircuitKey(Guid TenantId, MicrosoftProvider Provider)
{
    public override string ToString() => $"{Provider}:{TenantId}";
}
