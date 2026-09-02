namespace Mlcp.Domain.Capabilities;

/// <summary>
/// Why a capability is not available for a tenant. Unavailable data is always a typed reason,
/// never a null and never an empty chart: the UI renders the reason and its remediation
/// (CLAUDE.md rule 9, ADR-002).
/// </summary>
public enum CapabilityUnavailableReason
{
    /// <summary>Capability discovery has not run yet. Distinct from a discovered negative.</summary>
    NotDiscovered = 0,

    /// <summary>Tier-2 usage consent has not been granted. Remediable by an admin.</summary>
    Tier2NotGranted = 1,

    /// <summary>The tenant is not on a Microsoft Customer Agreement, so no price API exists.</summary>
    NotMca = 2,

    /// <summary>A billing account exists but our service principal holds no billing role on it.</summary>
    BillingRoleMissing = 3,

    /// <summary>Licences are sold through a CSP partner; Microsoft exposes no pricing to the customer.</summary>
    CspManaged = 4,

    /// <summary>Legacy web-direct agreement. Prices unlock when Microsoft migrates the account at renewal.</summary>
    Mosa = 5,

    /// <summary>No Azure subscriptions are visible to us in this tenant.</summary>
    NoAzure = 6,

    /// <summary>Azure subscriptions exist but Cost Management Reader has not been assigned.</summary>
    RbacMissing = 7,

    /// <summary>Classic CSP Azure offer. Cost data requires migration to an Azure Plan.</summary>
    ClassicCsp = 8,

    /// <summary>No billing account of any agreement type is reachable.</summary>
    NoBillingAccount = 9,

    /// <summary>Consent was revoked or app-only auth is failing. Sync is halted for this tenant.</summary>
    ConsentRevoked = 10,

    /// <summary>Partner Center features. Microsoft grants indirect resellers no API access.</summary>
    IndirectReseller = 11,

    /// <summary>Not a partner tenant, so the partner module does not apply.</summary>
    NotPartner = 12,

    /// <summary>Microsoft returned an error we could not classify. Retried on the next discovery run.</summary>
    ProviderError = 13,

    /// <summary>Report anonymisation is on, so named per-user data cannot be produced.</summary>
    UsageAnonymised = 14,
}
