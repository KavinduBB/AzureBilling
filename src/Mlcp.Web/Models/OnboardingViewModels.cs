using Mlcp.Application.Onboarding;

namespace Mlcp.Web.Models;

/// <summary>Where this instance is deployed. Set per region at deployment time.</summary>
/// <param name="Region">
/// Region code, stored on a tenant at connection and in the global region directory. A tenant's
/// region is never migrated silently, so this value is part of the deployment's identity
/// (docs/03-architecture.md §5.3, ADR-021).
/// </param>
public sealed record DeploymentOptions(string Region)
{
    public static DeploymentOptions Default { get; } = new("eu");
}

/// <summary>The region statement shown before an administrator connects (ADR-021).</summary>
/// <param name="Code">The region code submitted with the connect form.</param>
/// <param name="Name">How the region is described to people, for example "the EU".</param>
/// <param name="Alternatives">Other regions and the address to connect there instead.</param>
public sealed record RegionChoiceViewModel(string Code, string Name, IReadOnlyList<RegionLinkViewModel> Alternatives);

/// <param name="Code">Region code.</param>
/// <param name="Name">Display name.</param>
/// <param name="Url">This deployment's region picker route for that region.</param>
public sealed record RegionLinkViewModel(string Code, string Name, string Url);

/// <param name="DisplayName">The signed-in person, so the page can address them.</param>
/// <param name="IsDirectoryAdmin">Whether they can connect the organisation themselves.</param>
/// <param name="Region">The region statement for the connect form.</param>
/// <param name="PendingRequestSentTo">Set when a request to an admin is already outstanding.</param>
/// <param name="PendingRequestExpiresUtc">When that request stops working.</param>
public sealed record NotConnectedViewModel(
    string DisplayName,
    bool IsDirectoryAdmin,
    RegionChoiceViewModel Region,
    string? PendingRequestSentTo,
    DateTimeOffset? PendingRequestExpiresUtc);

/// <summary>Fixed reasons a consent attempt did not complete. Never built from query text.</summary>
public enum ConsentFailureReason
{
    /// <summary>The administrator declined, or closed the consent screen.</summary>
    Declined = 1,

    /// <summary>Entra reported an error on its side.</summary>
    EntraError = 2,

    /// <summary>The state was missing, expired, reused, or belonged to someone else.</summary>
    InvalidOrExpired = 3,

    /// <summary>The callback named a different organisation from the signed-in one.</summary>
    TenantMismatch = 4,

    /// <summary>The signed-in person is not a Global or Privileged Role Administrator.</summary>
    NotAdministrator = 5,

    /// <summary>Microsoft says MLCP is not consented in the organisation.</summary>
    NotGranted = 6,

    /// <summary>Consent could not be confirmed yet; a retry is queued.</summary>
    CouldNotConfirm = 7,
}

/// <param name="Reason">What went wrong, from a fixed set.</param>
public sealed record ConsentFailedViewModel(ConsentFailureReason Reason)
{
    /// <summary>The plain-language explanation for <see cref="Reason"/>.</summary>
    public string Message => Reason switch
    {
        ConsentFailureReason.Declined =>
            "Consent was not granted, so your organisation is not connected and nothing has changed.",
        ConsentFailureReason.EntraError =>
            "Microsoft could not complete the consent request. Nothing has changed; please try again.",
        ConsentFailureReason.InvalidOrExpired =>
            "This connection attempt has expired or was already used. Please start again from the connect page.",
        ConsentFailureReason.TenantMismatch =>
            "The consent was given for a different organisation from the one you are signed in to.",
        ConsentFailureReason.NotAdministrator =>
            "Only a Global Administrator or Privileged Role Administrator can connect your organisation.",
        ConsentFailureReason.NotGranted =>
            "Microsoft reports that MLCP has not been granted access to your organisation. Please try again.",
        ConsentFailureReason.CouldNotConfirm =>
            "We could not confirm the connection with Microsoft yet. We will keep checking for a few minutes; "
            + "you can also try again.",
        _ => "The connection did not complete.",
    };

    /// <summary>A stable reference for support. The enum name, never text from the request.</summary>
    public string Reference => Reason.ToString();
}

/// <param name="RequestedByUpn">Who in their organisation asked for the connection.</param>
/// <param name="ExpiresUtc">When the link stops working.</param>
/// <param name="IsDirectoryAdmin">Whether the visitor can connect.</param>
/// <param name="Region">The region statement for the connect form.</param>
public sealed record ConsentLandingViewModel(
    string RequestedByUpn,
    DateTimeOffset ExpiresUtc,
    bool IsDirectoryAdmin,
    RegionChoiceViewModel Region);

/// <param name="DeleteScheduledUtc">When the data is destroyed if the disconnect is not reversed.</param>
/// <param name="IsDirectoryAdmin">Whether the visitor may cancel the disconnect.</param>
public sealed record DisconnectedViewModel(DateTimeOffset? DeleteScheduledUtc, bool IsDirectoryAdmin);

/// <param name="TenantName">The organisation being disconnected.</param>
/// <param name="RetentionDays">How long data is kept before deletion.</param>
public sealed record DisconnectConfirmViewModel(string TenantName, int RetentionDays);

/// <param name="TenantId">Echoed in the "Check again" form so a stale tab cannot act on another tenant.</param>
/// <param name="Reason">The recorded reason, in plain language.</param>
/// <param name="Since">When the problem was first seen.</param>
/// <param name="IsDirectoryAdmin">Whether the visitor may check again or re-consent.</param>
/// <param name="Region">The region statement for the re-consent form.</param>
public sealed record NeedsReconsentViewModel(
    Guid TenantId,
    string Reason,
    DateTimeOffset? Since,
    bool IsDirectoryAdmin,
    RegionChoiceViewModel Region);

/// <param name="IsDirectoryAdmin">Whether the visitor can start consent again.</param>
/// <param name="Region">The region statement for the connect form.</param>
public sealed record FinishingConnectionViewModel(bool IsDirectoryAdmin, RegionChoiceViewModel Region);

/// <param name="Checklist">The unlock checklist.</param>
/// <param name="IsDirectoryAdmin">Whether the visitor may disconnect or start tier-2 consent.</param>
/// <param name="CanConnectUsageInsights">Whether the Usage Insights registration is configured.</param>
public sealed record ChecklistViewModel(OnboardingChecklist Checklist, bool IsDirectoryAdmin, bool CanConnectUsageInsights);

/// <summary>The form behind "Check again". The tenant id is a guard, never a selector.</summary>
public sealed record CheckAgainForm
{
    public Guid TenantId { get; init; }
}

/// <param name="Region">This stack's region.</param>
/// <param name="Alternatives">Other regions a visitor may pick.</param>
/// <param name="PreferredElsewhere">A region the visitor picked earlier that is not this one.</param>
public sealed record LandingViewModel(
    RegionLinkViewModel Region,
    IReadOnlyList<RegionLinkViewModel> Alternatives,
    RegionLinkViewModel? PreferredElsewhere);
