namespace Mlcp.Domain.Capabilities;

/// <summary>
/// The customer-facing onboarding guide that explains how to remediate a missing capability.
/// Maps one-to-one onto docs/07-onboarding-guides.md so guide copy and code cannot drift.
/// </summary>
public enum RemediationGuide
{
    /// <summary>Nothing the customer can do. The limitation is Microsoft's, not a missing grant.</summary>
    None = 0,

    /// <summary>Guide A — connect your organisation (Graph admin consent).</summary>
    ConnectOrganisation = 1,

    /// <summary>Guide B — unlock Azure costs (Cost Management Reader role assignment).</summary>
    AzureRbac = 2,

    /// <summary>Guide C — unlock prices and invoices (billing role).</summary>
    BillingRole = 3,

    /// <summary>Guide D — usage insights and the privacy setting.</summary>
    UsageInsights = 4,

    /// <summary>Guide E — licences that come from a partner.</summary>
    PartnerManaged = 5,

    /// <summary>Enter prices by hand instead. Always available as a fallback (ADR-006).</summary>
    ManualPricing = 6,
}

/// <summary>
/// Why a capability could not serve a request, and what the customer can do about it. Returned
/// in place of data, never alongside a null or an empty result set.
/// </summary>
/// <param name="Capability">The data source that is unavailable.</param>
/// <param name="Reason">The machine-readable cause, used for metrics and branching.</param>
/// <param name="Guide">The remediation guide to link to, if any.</param>
/// <param name="Detail">
/// Optional extra context discovered at probe time, such as which billing scope lacked a role.
/// Must never contain a token, SAS URL or other secret: it is rendered to users and logged.
/// </param>
public sealed record CapabilityUnavailable(
    Capability Capability,
    CapabilityUnavailableReason Reason,
    RemediationGuide Guide,
    string? Detail = null)
{
    /// <summary>True when a customer action can plausibly unlock this capability.</summary>
    public bool IsRemediable => Guide is not RemediationGuide.None;

    public static CapabilityUnavailable NotDiscovered(Capability capability)
        => new(capability, CapabilityUnavailableReason.NotDiscovered, RemediationGuide.ConnectOrganisation);

    public static CapabilityUnavailable ConsentRevoked(Capability capability)
        => new(capability, CapabilityUnavailableReason.ConsentRevoked, RemediationGuide.ConnectOrganisation);

    public static CapabilityUnavailable ProviderError(Capability capability, string? detail = null)
        => new(capability, CapabilityUnavailableReason.ProviderError, RemediationGuide.None, detail);
}
