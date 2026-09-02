namespace Mlcp.Web.Models;

/// <summary>Where this instance is deployed. Set per region at deployment time.</summary>
/// <param name="Region">
/// Azure region name, stored on a tenant at connection. A tenant's region is never migrated
/// silently, so this value is part of the deployment's identity (docs/03-architecture.md §5.3).
/// </param>
public sealed record DeploymentOptions(string Region)
{
    public static DeploymentOptions Default { get; } = new("westeurope");
}

/// <param name="DisplayName">The signed-in person, so the page can address them.</param>
/// <param name="PendingRequestSentTo">Set when a request to an admin is already outstanding.</param>
/// <param name="PendingRequestExpiresUtc">When that request stops working.</param>
public sealed record NotConnectedViewModel(
    string DisplayName,
    string? PendingRequestSentTo,
    DateTimeOffset? PendingRequestExpiresUtc);

/// <param name="Error">The Entra error code, for support to correlate against.</param>
/// <param name="Description">Plain-language explanation shown to the administrator.</param>
public sealed record ConsentDeclinedViewModel(string Error, string? Description);

/// <param name="RequestedByUpn">Who in their organisation asked for the connection.</param>
/// <param name="ExpiresUtc">When the link stops working.</param>
public sealed record ConsentLandingViewModel(string RequestedByUpn, DateTimeOffset ExpiresUtc);

/// <param name="DeleteScheduledUtc">When the data is destroyed if the disconnect is not reversed.</param>
public sealed record DisconnectedViewModel(DateTimeOffset? DeleteScheduledUtc);
