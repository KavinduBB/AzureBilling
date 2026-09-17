namespace Mlcp.Domain.Tenancy;

/// <summary>Lifecycle of a connected customer tenant (docs/03-architecture.md §3).</summary>
public enum TenantStatus
{
    Unknown = 0,

    /// <summary>Admin consent received; capability discovery has not completed.</summary>
    Provisioning = 1,

    /// <summary>Discovery complete; sync jobs run on schedule.</summary>
    Active = 2,

    /// <summary>Consent revoked or app-only auth failing. Sync halted, admin notified.</summary>
    NeedsReconsent = 3,

    /// <summary>Disconnected by the customer; data retained until <c>DeleteScheduledUtc</c>.</summary>
    GracePeriod = 4,

    /// <summary>
    /// Transient in-memory marker only: deletion removes the tenant row, and the
    /// <c>DeletionCertificate</c> is the durable record (ADR-019).
    /// </summary>
    Deleted = 5,

    /// <summary>Someone from the tenant has signed in; no admin consent yet (docs/03 §3).</summary>
    NotConnected = 6,

    /// <summary>
    /// An admin returned from the consent screen; waiting for an app-only call to confirm it
    /// (service-principal propagation, ADR-018).
    /// </summary>
    ConsentPendingVerification = 7,
}

/// <summary>
/// Commercial agreement governing the tenant's Microsoft purchases. Determines which
/// data sources can ever become available (docs/01-scope-and-scenarios.md §4).
/// </summary>
public enum AgreementType
{
    /// <summary>Not yet discovered. Not the same as <see cref="Unknown"/>.</summary>
    NotDiscovered = 0,

    /// <summary>Microsoft Online Subscription Agreement — legacy web-direct. Scenario A.</summary>
    Mosa = 1,

    /// <summary>Microsoft Customer Agreement — richest API surface. Scenario B.</summary>
    Mca = 2,

    /// <summary>Enterprise Agreement. Scenario C.</summary>
    Ea = 3,

    /// <summary>Microsoft Partner Agreement — a CSP partner's own billing account. Scenario E.</summary>
    Mpa = 4,

    /// <summary>Licences are sold by a CSP partner; no billing relationship with Microsoft. Scenario D.</summary>
    CspManaged = 5,

    /// <summary>Discovery ran but could not classify the tenant.</summary>
    Unknown = 6,

    /// <summary>
    /// The agreement cannot be seen with the grants we hold (no Azure or billing visibility).
    /// Not evidence of MOSA (ADR-020).
    /// </summary>
    Undetermined = 7,
}

/// <summary>Steps of the onboarding state machine. Persisted one row per tenant per step.</summary>
public enum OnboardingStepName
{
    Unknown = 0,
    TenantRegistered = 1,
    AdminConsentRequested = 2,
    AdminConsentGranted = 3,
    CapabilityDiscovery = 4,
    FloorSyncPrimed = 5,
}

public enum OnboardingStepStatus
{
    Unknown = 0,
    Pending = 1,
    InProgress = 2,
    Completed = 3,
    Failed = 4,
    Skipped = 5,
}

/// <summary>In-application role, distinct from any Microsoft role (docs/03-architecture.md §4.4).</summary>
public enum AppRole
{
    Unknown = 0,

    /// <summary>Full read, manages application users and settings.</summary>
    Owner = 1,

    /// <summary>Full read plus manual price entry.</summary>
    Analyst = 2,

    /// <summary>Dashboards only.</summary>
    Viewer = 3,

    /// <summary>Phase 5 write operations. Always additional to a read role.</summary>
    SubscriptionManager = 4,
}
