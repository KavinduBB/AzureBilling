namespace Mlcp.Application.Onboarding;

/// <summary>Outcome of confirming a tenant's admin consent with an app-only call (ADR-018).</summary>
public enum ConsentVerificationStatus
{
    /// <summary>An app-only call to the tenant succeeded: consent exists.</summary>
    Verified = 0,

    /// <summary>The service principal has not propagated yet. Retry shortly.</summary>
    PendingPropagation = 1,

    /// <summary>Microsoft says the app is not consented in this tenant.</summary>
    NotGranted = 2,

    /// <summary>Transient or unclassified failure. Retry; do not conclude anything.</summary>
    Failed = 3,
}

public sealed record ConsentVerificationResult(ConsentVerificationStatus Status, string? Detail = null);

/// <summary>
/// Confirms tenant-wide admin consent by calling Microsoft as the core app, never by trusting
/// the consent callback's query string (ADR-018). Implemented in Mlcp.Integration.Graph.
/// </summary>
public interface IConsentVerifier
{
    Task<ConsentVerificationResult> VerifyAsync(Guid tenantId, CancellationToken cancellationToken);
}
