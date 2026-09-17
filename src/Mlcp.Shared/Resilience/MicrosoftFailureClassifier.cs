using Mlcp.Shared.Identity;

namespace Mlcp.Shared.Resilience;

/// <summary>
/// What a failed Microsoft call means, with a fixed effect per kind (ADR-016).
/// </summary>
public enum MicrosoftFailureKind
{
    /// <summary>The call succeeded (2xx, or 3xx where a redirect is the answer).</summary>
    None = 0,

    /// <summary>The core app's grant is gone. Tenant → NeedsReconsent; floor → ConsentRevoked. Not retried.</summary>
    GrantRevoked = 1,

    /// <summary>403 on a core Graph floor call. Tenant → NeedsReconsent; GraphLicensing → ConsentRevoked. Not retried.</summary>
    FloorPermissionRemoved = 2,

    /// <summary>401/403 on a non-floor call, or a Usage Insights consent error. Only that capability degrades. Not retried.</summary>
    CapabilityDenied = 3,

    /// <summary>408, 429, 5xx, timeouts, open circuit, network, token endpoint unreachable. Previous verdict kept. Retried.</summary>
    Transient = 4,

    /// <summary>MLCP's own credential failed. No tenant effect; the app is paused process-wide. Operator alert.</summary>
    PlatformCredential = 5,

    /// <summary>404. Probe-specific meaning (for example no billing account). Not retried.</summary>
    NotFound = 6,

    /// <summary>400 or another 4xx. No tenant effect. Not retried.</summary>
    OtherClientError = 7,
}

/// <summary>
/// The single place a Microsoft failure is classified (ADR-016). Pure and exhaustively tested
/// (<c>MicrosoftFailureClassifierTests</c>).
/// </summary>
public static class MicrosoftFailureClassifier
{
    /// <summary>
    /// After consent, the service principal can take a few minutes to appear. Missing-principal
    /// errors inside this window are transient (ADR-016 rule 3).
    /// </summary>
    public static TimeSpan PropagationWindow { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Token-endpoint codes meaning "this app has no service principal in the tenant".</summary>
    private static readonly HashSet<string> MissingPrincipalCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AADSTS700016",
        "AADSTS7000229",
    };

    /// <summary>Token-endpoint codes meaning the grant is gone regardless of timing.</summary>
    private static readonly HashSet<string> LostGrantCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AADSTS7000112", // application disabled in the tenant
        "AADSTS90002",   // tenant not found
        "AADSTS65001",   // consent does not exist (docs/02 §4)
        "invalid_grant",
    };

    /// <summary>Classifies an HTTP response status.</summary>
    /// <param name="statusCode">The HTTP status Microsoft returned.</param>
    /// <param name="app">The registration whose token was used.</param>
    /// <param name="isFloorCall">
    /// The call is one of the core Graph floor calls (<c>subscribedSkus</c>,
    /// <c>directory/subscriptions</c>, <c>organization</c>).
    /// </param>
    public static MicrosoftFailureKind ClassifyStatus(int statusCode, MicrosoftApp app, bool isFloorCall)
    {
        if (statusCode is >= 100 and < 400)
        {
            return MicrosoftFailureKind.None;
        }

        var floor = isFloorCall && app == MicrosoftApp.Core;

        return statusCode switch
        {
            401 => floor ? MicrosoftFailureKind.GrantRevoked : MicrosoftFailureKind.CapabilityDenied,
            403 => floor ? MicrosoftFailureKind.FloorPermissionRemoved : MicrosoftFailureKind.CapabilityDenied,
            404 => MicrosoftFailureKind.NotFound,
            408 or 429 => MicrosoftFailureKind.Transient,
            >= 500 => MicrosoftFailureKind.Transient,
            _ => MicrosoftFailureKind.OtherClientError,
        };
    }

    /// <summary>Classifies a token-endpoint failure.</summary>
    /// <param name="failure">The reduced failure from the token provider.</param>
    /// <param name="app">The registration the token was requested for.</param>
    /// <param name="consentCallbackUtc">When an admin last returned from the consent screen, if known.</param>
    /// <param name="nowUtc">The current time.</param>
    public static MicrosoftFailureKind ClassifyTokenFailure(
        TokenFailure failure,
        MicrosoftApp app,
        DateTimeOffset? consentCallbackUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(failure);

        switch (failure.Category)
        {
            case TokenFailureCategory.Transport:
                return MicrosoftFailureKind.Transient;

            case TokenFailureCategory.PlatformCredential:
            case TokenFailureCategory.Paused:
            case TokenFailureCategory.NotConfigured:
                return MicrosoftFailureKind.PlatformCredential;

            case TokenFailureCategory.Unknown:
                return failure.StatusCode is >= 400 and < 500 and not 408 and not 429
                    ? MicrosoftFailureKind.OtherClientError
                    : MicrosoftFailureKind.Transient;
        }

        var code = failure.ErrorCode;

        // A platform code can arrive classified as Refused only if the caller built the failure by
        // hand; honour the code rather than the category.
        if (TokenFailure.IsPlatformCode(code))
        {
            return MicrosoftFailureKind.PlatformCredential;
        }

        if (IsMissingPrincipal(code))
        {
            if (app == MicrosoftApp.Core)
            {
                return IsWithinPropagationWindow(consentCallbackUtc, nowUtc)
                    ? MicrosoftFailureKind.Transient
                    : MicrosoftFailureKind.GrantRevoked;
            }

            // No Usage Insights principal means tier 2 was never granted (ADR-015).
            return MicrosoftFailureKind.CapabilityDenied;
        }

        if (code is not null && LostGrantCodes.Contains(code))
        {
            return app == MicrosoftApp.Core ? MicrosoftFailureKind.GrantRevoked : MicrosoftFailureKind.CapabilityDenied;
        }

        // Any other customer-side AADSTS (for example a Conditional Access block) changes nothing
        // on the tenant and is not worth retrying.
        return MicrosoftFailureKind.OtherClientError;
    }

    /// <summary>
    /// Classifies an exception thrown by the pipeline itself (not a token failure): network
    /// errors, per-attempt timeouts, an open circuit and a full concurrency queue are all
    /// transient. Anything unrecognised is treated as transient too, so a surprise never
    /// downgrades a capability or flags a tenant.
    /// </summary>
    public static MicrosoftFailureKind ClassifyException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is TokenAcquisitionException)
        {
            throw new ArgumentException(
                "Token failures need the app and the propagation window; use ClassifyTokenFailure.",
                nameof(exception));
        }

        // HttpRequestException, TimeoutRejectedException, BrokenCircuitException,
        // RateLimiterRejectedException, TimeoutException and TaskCanceledException all land here.
        return MicrosoftFailureKind.Transient;
    }

    /// <summary>True for the codes that mean the app has no service principal in the tenant.</summary>
    public static bool IsMissingPrincipal(string? errorCode)
        => errorCode is not null && MissingPrincipalCodes.Contains(errorCode);

    /// <summary>True when <paramref name="nowUtc"/> is within <see cref="PropagationWindow"/> of the consent callback.</summary>
    public static bool IsWithinPropagationWindow(DateTimeOffset? consentCallbackUtc, DateTimeOffset nowUtc)
        => consentCallbackUtc is { } consented
            && nowUtc - consented < PropagationWindow;

    /// <summary>Only these kinds change <c>Tenant.Status</c> (ADR-016 rule 2).</summary>
    public static bool RequiresReconsent(MicrosoftFailureKind kind)
        => kind is MicrosoftFailureKind.GrantRevoked or MicrosoftFailureKind.FloorPermissionRemoved;

    /// <summary>Only transient failures are retried.</summary>
    public static bool IsRetryable(MicrosoftFailureKind kind) => kind == MicrosoftFailureKind.Transient;
}
