using Mlcp.Domain.Common;

namespace Mlcp.Domain.Tenancy;

/// <summary>
/// A connected customer tenant. The primary key is the Entra <c>tid</c> itself, so the
/// natural key and the surrogate key are the same value and cannot drift.
/// </summary>
public class Tenant : ITenantScoped
{
    private readonly List<OnboardingStep> _onboardingSteps = [];

    public Guid TenantId { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string? DefaultDomain { get; private set; }

    /// <summary>Deployment region chosen at connection. Never migrated silently (docs/03 §5.3).</summary>
    public string Region { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    public DateTimeOffset? ConsentGrantedUtc { get; private set; }

    public Guid? ConsentGrantedByObjectId { get; private set; }

    public AgreementType AgreementTypePrimary { get; private set; }

    public bool IsPartnerManaged { get; private set; }

    public Guid? ManagingPartnerTenantId { get; private set; }

    /// <summary>Per-tenant feature flags, serialised as JSON.</summary>
    public TenantFeatures Features { get; private set; } = TenantFeatures.Default;

    public DateTimeOffset? DeleteScheduledUtc { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    public byte[]? RowVersion { get; private set; }

    public IReadOnlyCollection<OnboardingStep> OnboardingSteps => _onboardingSteps.AsReadOnly();

    private Tenant()
    {
    }

    /// <summary>
    /// Records a tenant at first sign-in, before any consent. The tenant exists in
    /// <see cref="TenantStatus.Provisioning"/> only once an admin has consented; until then
    /// callers should treat it as not connected.
    /// </summary>
    public static Tenant Register(Guid tenantId, string displayName, string? defaultDomain, string region, DateTimeOffset nowUtc)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        return new Tenant
        {
            TenantId = tenantId,
            DisplayName = displayName,
            DefaultDomain = defaultDomain,
            Region = region,
            Status = TenantStatus.Provisioning,
            AgreementTypePrimary = AgreementType.NotDiscovered,
            Features = TenantFeatures.Default,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };
    }

    /// <summary>Records the admin consent callback. Idempotent: re-consent refreshes the grant.</summary>
    public void GrantConsent(Guid grantedByObjectId, DateTimeOffset nowUtc)
    {
        if (Status == TenantStatus.Deleted)
        {
            throw new DomainException("Cannot grant consent on a deleted tenant.");
        }

        ConsentGrantedUtc = nowUtc;
        ConsentGrantedByObjectId = grantedByObjectId;
        Status = TenantStatus.Provisioning;
        DeleteScheduledUtc = null;
        UpdatedUtc = nowUtc;
    }

    /// <summary>Discovery succeeded and the floor is syncing.</summary>
    public void Activate(DateTimeOffset nowUtc)
    {
        if (ConsentGrantedUtc is null)
        {
            throw new DomainException("Cannot activate a tenant that has not granted consent.");
        }

        Status = TenantStatus.Active;
        UpdatedUtc = nowUtc;
    }

    /// <summary>
    /// Consent was revoked or app-only auth returned 401/403. Sync must stop; the tenant's
    /// existing data is retained and remains readable.
    /// </summary>
    public void MarkNeedsReconsent(DateTimeOffset nowUtc)
    {
        if (Status is TenantStatus.Deleted or TenantStatus.GracePeriod)
        {
            return;
        }

        Status = TenantStatus.NeedsReconsent;
        UpdatedUtc = nowUtc;
    }

    /// <summary>Customer-initiated disconnect. Starts the retention clock (docs/03 §8).</summary>
    public void BeginGracePeriod(DateTimeOffset nowUtc, TimeSpan retention)
    {
        if (Status == TenantStatus.Deleted)
        {
            throw new DomainException("Tenant is already deleted.");
        }

        Status = TenantStatus.GracePeriod;
        DeleteScheduledUtc = nowUtc + retention;
        UpdatedUtc = nowUtc;
    }

    /// <summary>Reverses a disconnect while still inside the grace period.</summary>
    public void CancelDeletion(DateTimeOffset nowUtc)
    {
        if (Status != TenantStatus.GracePeriod)
        {
            throw new DomainException("Only a tenant in the grace period can cancel deletion.");
        }

        Status = ConsentGrantedUtc is null ? TenantStatus.Provisioning : TenantStatus.Active;
        DeleteScheduledUtc = null;
        UpdatedUtc = nowUtc;
    }

    public void MarkDeleted(DateTimeOffset nowUtc)
    {
        Status = TenantStatus.Deleted;
        UpdatedUtc = nowUtc;
    }

    public void SetAgreementType(AgreementType agreementType, DateTimeOffset nowUtc)
    {
        AgreementTypePrimary = agreementType;
        UpdatedUtc = nowUtc;
    }

    public void SetPartnerManagement(bool isPartnerManaged, Guid? managingPartnerTenantId, DateTimeOffset nowUtc)
    {
        IsPartnerManaged = isPartnerManaged;
        ManagingPartnerTenantId = isPartnerManaged ? managingPartnerTenantId : null;
        UpdatedUtc = nowUtc;
    }

    public void UpdateDirectoryDetails(string displayName, string? defaultDomain, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName;
        DefaultDomain = defaultDomain;
        UpdatedUtc = nowUtc;
    }

    public void SetFeatures(TenantFeatures features, DateTimeOffset nowUtc)
    {
        Features = features ?? throw new ArgumentNullException(nameof(features));
        UpdatedUtc = nowUtc;
    }

    /// <summary>True when sync jobs are permitted to call Microsoft for this tenant.</summary>
    public bool IsSyncEligible => Status is TenantStatus.Provisioning or TenantStatus.Active;
}
