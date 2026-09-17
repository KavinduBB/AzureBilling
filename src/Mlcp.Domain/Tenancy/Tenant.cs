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

    /// <summary>When the customer disconnected. Distinct from <see cref="DeleteScheduledUtc"/>.</summary>
    public DateTimeOffset? DisconnectedUtc { get; private set; }

    /// <summary>
    /// When an administrator last returned from the Entra consent screen. Used for the
    /// service-principal propagation window (ADR-016 rule 3); not proof of consent.
    /// </summary>
    public DateTimeOffset? ConsentCallbackUtc { get; private set; }

    /// <summary>Why the tenant needs re-consent (ADR-016 classification). Null otherwise.</summary>
    public string? NeedsReconsentReason { get; private set; }

    public DateTimeOffset? NeedsReconsentSinceUtc { get; private set; }

    /// <summary>When the automatic floor re-probe should next run (ADR-016 rule 5).</summary>
    public DateTimeOffset? NextReconsentProbeUtc { get; private set; }

    public int ReconsentProbeAttempts { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    public byte[]? RowVersion { get; private set; }

    public IReadOnlyCollection<OnboardingStep> OnboardingSteps => _onboardingSteps.AsReadOnly();

    private Tenant()
    {
    }

    /// <summary>
    /// Records a tenant at first sign-in, before any consent. The tenant is
    /// <see cref="TenantStatus.NotConnected"/> until consent is verified (ADR-018).
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
            Status = TenantStatus.NotConnected,
            AgreementTypePrimary = AgreementType.NotDiscovered,
            Features = TenantFeatures.Default,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };
    }

    /// <summary>
    /// An administrator returned from the consent screen, but consent has not yet been
    /// confirmed with an app-only call (ADR-018). Never changes a tenant in its grace period.
    /// </summary>
    public void AwaitConsentVerification(Guid administratorObjectId, DateTimeOffset nowUtc)
    {
        EnsureNotDeletedOrDisconnected();

        ConsentCallbackUtc = nowUtc;
        ConsentGrantedByObjectId = administratorObjectId;

        if (Status is TenantStatus.NotConnected or TenantStatus.Unknown)
        {
            Status = TenantStatus.ConsentPendingVerification;
        }

        UpdatedUtc = nowUtc;
    }

    /// <summary>
    /// An app-only call to the tenant succeeded, so consent is real (ADR-018). Idempotent.
    /// A tenant that needed re-consent returns straight to <see cref="TenantStatus.Active"/>.
    /// Never cancels a scheduled deletion: that is <see cref="CancelDeletion"/>, Owner-only.
    /// </summary>
    public void ConfirmConsent(Guid grantedByObjectId, DateTimeOffset nowUtc)
    {
        EnsureNotDeletedOrDisconnected();

        ConsentGrantedUtc = nowUtc;
        ConsentGrantedByObjectId = grantedByObjectId;

        Status = Status switch
        {
            TenantStatus.NeedsReconsent => TenantStatus.Active,
            TenantStatus.Active => TenantStatus.Active,
            _ => TenantStatus.Provisioning,
        };

        ClearReconsentState();
        UpdatedUtc = nowUtc;
    }

    /// <summary>Discovery succeeded and the floor is syncing.</summary>
    public void Activate(DateTimeOffset nowUtc)
    {
        if (ConsentGrantedUtc is null)
        {
            throw new DomainException("Cannot activate a tenant that has not granted consent.");
        }

        if (Status is not (TenantStatus.Provisioning or TenantStatus.Active))
        {
            throw new DomainException($"Cannot activate a tenant in status {Status}.");
        }

        Status = TenantStatus.Active;
        UpdatedUtc = nowUtc;
    }

    /// <summary>
    /// The core grant was lost or a Graph floor call was refused (ADR-016). Sync stops; data is
    /// retained and readable; automatic re-probes are scheduled. Repeated calls keep the
    /// original <see cref="NeedsReconsentSinceUtc"/>.
    /// </summary>
    public void MarkNeedsReconsent(string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is not (TenantStatus.Provisioning or TenantStatus.Active or TenantStatus.NeedsReconsent))
        {
            return;
        }

        if (Status != TenantStatus.NeedsReconsent)
        {
            NeedsReconsentSinceUtc = nowUtc;
            ReconsentProbeAttempts = 0;
            NextReconsentProbeUtc = nowUtc + ReconsentProbeDelay(0, TimeSpan.Zero);
        }

        Status = TenantStatus.NeedsReconsent;
        NeedsReconsentReason = reason;
        UpdatedUtc = nowUtc;
    }

    /// <summary>An automatic floor re-probe still failed. Schedules the next one (ADR-016 rule 5).</summary>
    public void RecordReconsentProbeFailed(DateTimeOffset nowUtc)
    {
        if (Status != TenantStatus.NeedsReconsent)
        {
            return;
        }

        ReconsentProbeAttempts++;
        var elapsed = nowUtc - (NeedsReconsentSinceUtc ?? nowUtc);
        NextReconsentProbeUtc = nowUtc + ReconsentProbeDelay(ReconsentProbeAttempts, elapsed);
        UpdatedUtc = nowUtc;
    }

    /// <summary>A floor re-probe succeeded without the customer visiting MLCP.</summary>
    public void RestoreConsent(DateTimeOffset nowUtc)
    {
        if (Status != TenantStatus.NeedsReconsent)
        {
            return;
        }

        Status = TenantStatus.Active;
        ClearReconsentState();
        UpdatedUtc = nowUtc;
    }

    /// <summary>True when an automatic re-probe is due.</summary>
    public bool IsDueForReconsentProbe(DateTimeOffset nowUtc)
        => Status == TenantStatus.NeedsReconsent
            && NextReconsentProbeUtc is { } due
            && due <= nowUtc;

    /// <summary>+1 h, +6 h, +24 h, then daily for 30 days, then weekly (ADR-016 rule 5).</summary>
    public static TimeSpan ReconsentProbeDelay(int attemptsSoFar, TimeSpan elapsedSinceFlagged)
        => attemptsSoFar switch
        {
            0 => TimeSpan.FromHours(1),
            1 => TimeSpan.FromHours(6),
            2 => TimeSpan.FromHours(24),
            _ when elapsedSinceFlagged < TimeSpan.FromDays(30) => TimeSpan.FromDays(1),
            _ => TimeSpan.FromDays(7),
        };

    /// <summary>
    /// Customer-initiated disconnect. Starts the retention clock (docs/03 §8). Idempotent: a
    /// repeated disconnect does not push the deletion date back.
    /// </summary>
    public void BeginGracePeriod(DateTimeOffset nowUtc, TimeSpan retention)
    {
        if (Status == TenantStatus.Deleted)
        {
            throw new DomainException("Tenant is already deleted.");
        }

        if (Status == TenantStatus.GracePeriod)
        {
            return;
        }

        Status = TenantStatus.GracePeriod;
        DisconnectedUtc = nowUtc;
        DeleteScheduledUtc = nowUtc + retention;
        UpdatedUtc = nowUtc;
    }

    /// <summary>Reverses a disconnect while still inside the grace period. Owner-only (ADR-018).</summary>
    public void CancelDeletion(DateTimeOffset nowUtc)
    {
        if (Status != TenantStatus.GracePeriod)
        {
            throw new DomainException("Only a tenant in the grace period can cancel deletion.");
        }

        Status = ConsentGrantedUtc is null ? TenantStatus.NotConnected : TenantStatus.Active;
        DeleteScheduledUtc = null;
        DisconnectedUtc = null;
        UpdatedUtc = nowUtc;
    }

    /// <summary>True when the scheduled deletion is due. Re-checked inside the delete transaction.</summary>
    public bool IsDueForDeletion(DateTimeOffset nowUtc)
        => Status == TenantStatus.GracePeriod
            && DeleteScheduledUtc is { } due
            && due <= nowUtc;

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

    private void EnsureNotDeletedOrDisconnected()
    {
        if (Status == TenantStatus.Deleted)
        {
            throw new DomainException("Cannot change consent on a deleted tenant.");
        }

        if (Status == TenantStatus.GracePeriod)
        {
            throw new DomainException("The tenant is disconnected. An Owner must cancel the disconnect first.");
        }
    }

    private void ClearReconsentState()
    {
        NeedsReconsentReason = null;
        NeedsReconsentSinceUtc = null;
        NextReconsentProbeUtc = null;
        ReconsentProbeAttempts = 0;
    }
}
