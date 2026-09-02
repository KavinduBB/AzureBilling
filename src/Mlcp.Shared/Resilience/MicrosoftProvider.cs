namespace Mlcp.Shared.Resilience;

/// <summary>
/// A Microsoft API surface with its own throttling behaviour and its own failure domain.
/// Circuit breakers and concurrency limits are keyed per provider so that, for example, Cost
/// Management being throttled does not stop licence data from syncing.
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

/// <summary>
/// Raised when Microsoft returns 401 or 403 for a tenant's app-only credential, meaning
/// consent was revoked or a required role was removed. Retrying cannot help: the tenant is
/// flagged <c>NeedsReconsent</c>, sync halts, and the admin is notified (CLAUDE.md rule 7).
/// </summary>
public class NeedsReconsentException : Exception
{
    public NeedsReconsentException()
        : base("Microsoft rejected the credential for this tenant.")
    {
    }

    public NeedsReconsentException(string message)
        : base(message)
    {
    }

    public NeedsReconsentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NeedsReconsentException(Guid tenantId, MicrosoftProvider provider, int statusCode)
        : base($"Microsoft returned {statusCode} for provider {provider}. The tenant must re-consent or restore the missing role.")
    {
        TenantId = tenantId;
        Provider = provider;
        StatusCode = statusCode;
    }

    public Guid TenantId { get; }

    public MicrosoftProvider Provider { get; }

    public int StatusCode { get; }
}

/// <summary>
/// Notified when a tenant's credential stops working, so the tenant can be moved to
/// <c>NeedsReconsent</c> and its scheduled jobs suspended. Implemented in the persistence
/// layer; declared here so the resilience pipeline can raise it without depending upwards.
/// </summary>
public interface ITenantReconsentSignal
{
    Task SignalNeedsReconsentAsync(
        Guid tenantId,
        MicrosoftProvider provider,
        int statusCode,
        CancellationToken cancellationToken);
}
