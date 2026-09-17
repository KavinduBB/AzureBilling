using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.Identity.Client;

namespace Mlcp.Shared.Identity;

/// <summary>What kind of token-endpoint failure occurred, before any tenant meaning is attached.</summary>
public enum TokenFailureCategory
{
    Unknown = 0,

    /// <summary>The token endpoint was unreachable, timed out, throttled or returned 5xx.</summary>
    Transport = 1,

    /// <summary>The endpoint answered with a customer-side refusal (an AADSTS code or <c>invalid_grant</c>).</summary>
    Refused = 2,

    /// <summary>MLCP's own credential was rejected or could not be loaded (ADR-016 PlatformCredential).</summary>
    PlatformCredential = 3,

    /// <summary>The app is paused process-wide after a platform credential failure.</summary>
    Paused = 4,

    /// <summary>The app registration is not configured in this host.</summary>
    NotConfigured = 5,
}

/// <summary>A token-endpoint failure, reduced to what classification needs.</summary>
/// <param name="ErrorCode">
/// The <c>AADSTS</c> code (for example <c>AADSTS700016</c>) when present, otherwise the OAuth error
/// (<c>invalid_grant</c>, <c>invalid_client</c>), otherwise null.
/// </param>
/// <param name="Category">The coarse category.</param>
/// <param name="StatusCode">HTTP status from the token endpoint, when known.</param>
public sealed record TokenFailure(string? ErrorCode, TokenFailureCategory Category, int? StatusCode = null)
{
    /// <summary>AADSTS codes that mean MLCP's own credential is wrong (ADR-016).</summary>
    private static readonly HashSet<string> PlatformCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AADSTS7000215", // invalid client secret
        "AADSTS7000222", // client secret expired
        "AADSTS700027",  // client assertion failed signature validation
        "AADSTS700024",  // client assertion outside its validity window (clock or certificate)
    };

    private static readonly Regex AadstsCode = new(@"AADSTS\d+", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>Whether <paramref name="errorCode"/> is one of the platform-credential codes.</summary>
    public static bool IsPlatformCode(string? errorCode)
        => errorCode is not null
            && (PlatformCodes.Contains(errorCode) || string.Equals(errorCode, "invalid_client", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reduces an exception thrown while acquiring a token to a <see cref="TokenFailure"/>.
    /// </summary>
    /// <remarks>
    /// AADSTS codes are matched before the OAuth error string: <c>AADSTS7000229</c> (service
    /// principal missing, a customer-side state) arrives with <c>invalid_client</c>, which on its
    /// own would read as our credential being wrong.
    /// </remarks>
    public static TokenFailure FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is CredentialUnavailableException)
        {
            return new TokenFailure("CredentialUnavailable", TokenFailureCategory.PlatformCredential);
        }

        int? status = null;
        string? oauthError = null;
        string? aadsts = null;
        var transport = false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case MsalServiceException msal:
                    status ??= msal.StatusCode == 0 ? null : msal.StatusCode;
                    oauthError ??= msal.ErrorCode;
                    break;
                case HttpRequestException or TimeoutException or TaskCanceledException:
                    transport = true;
                    break;
            }

            aadsts ??= FindAadsts(current.Message);
        }

        if (aadsts is not null)
        {
            return new TokenFailure(
                aadsts,
                IsPlatformCode(aadsts) ? TokenFailureCategory.PlatformCredential : TokenFailureCategory.Refused,
                status);
        }

        var message = exception.Message;

        if (string.Equals(oauthError, "invalid_client", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
        {
            return new TokenFailure("invalid_client", TokenFailureCategory.PlatformCredential, status);
        }

        if (string.Equals(oauthError, "invalid_grant", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
        {
            return new TokenFailure("invalid_grant", TokenFailureCategory.Refused, status);
        }

        if (transport || status is 408 or 429 or >= 500)
        {
            return new TokenFailure(oauthError, TokenFailureCategory.Transport, status);
        }

        return new TokenFailure(oauthError, TokenFailureCategory.Unknown, status);
    }

    private static string? FindAadsts(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var match = AadstsCode.Match(message);
        return match.Success ? match.Value : null;
    }
}

/// <summary>
/// Raised when an app-only token cannot be acquired. Carries the raw failure and the app; the
/// meaning for the tenant is decided by <see cref="Mlcp.Shared.Resilience.MicrosoftFailureClassifier"/>.
/// </summary>
public sealed class TokenAcquisitionException : Exception
{
    public TokenAcquisitionException()
        : this(MicrosoftApp.Unknown, new TokenFailure(null, TokenFailureCategory.Unknown), "Token acquisition failed.")
    {
    }

    public TokenAcquisitionException(string message)
        : this(MicrosoftApp.Unknown, new TokenFailure(null, TokenFailureCategory.Unknown), message)
    {
    }

    public TokenAcquisitionException(string message, Exception innerException)
        : this(MicrosoftApp.Unknown, TokenFailure.FromException(innerException), message, innerException)
    {
    }

    public TokenAcquisitionException(MicrosoftApp app, TokenFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        App = app;
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public MicrosoftApp App { get; }

    public TokenFailure Failure { get; }

    /// <summary>For <see cref="TokenFailureCategory.Paused"/>: when the pause ends.</summary>
    public DateTimeOffset? PausedUntilUtc { get; init; }
}
