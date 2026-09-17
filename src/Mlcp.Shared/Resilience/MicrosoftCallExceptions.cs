using Mlcp.Shared.Identity;

namespace Mlcp.Shared.Resilience;

/// <summary>
/// Base for every typed failure thrown by <see cref="Mlcp.Shared.Http.MicrosoftApiClient"/>'s
/// throwing methods. Carries the ADR-016 classification. Throwing never changes a tenant: the
/// service owning the tenant aggregate decides what to do (ADR-016 rule 2).
/// </summary>
/// <remarks>
/// Hierarchy:
/// <code>
/// MicrosoftCallException
///   MicrosoftCallRefusedException          CapabilityDenied (and base of the two below)
///     NeedsReconsentException              GrantRevoked, FloorPermissionRemoved
///   MicrosoftTransientException            Transient (retries exhausted, circuit open)
///     MicrosoftThrottledException          Transient with a retry hint: re-queue at RetryAfter
///   MicrosoftPlatformCredentialException   PlatformCredential: operator problem, re-queue later
/// </code>
/// </remarks>
public class MicrosoftCallException : Exception
{
    public MicrosoftCallException()
        : base("A Microsoft call failed.")
    {
    }

    public MicrosoftCallException(string message)
        : base(message)
    {
    }

    public MicrosoftCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MicrosoftCallException(
        MicrosoftFailureKind kind,
        Guid tenantId,
        MicrosoftProvider provider,
        MicrosoftApp app,
        int? statusCode,
        string? errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        TenantId = tenantId;
        Provider = provider;
        App = app;
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public MicrosoftFailureKind Kind { get; init; }

    public Guid TenantId { get; init; }

    public MicrosoftProvider Provider { get; init; }

    public MicrosoftApp App { get; init; }

    /// <summary>HTTP status from the API, or null for a token-endpoint or transport failure.</summary>
    public int? StatusCode { get; init; }

    /// <summary>AADSTS code, OAuth error, or the API's <c>error.code</c>, when known.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>True when the tenant must be moved to NeedsReconsent by its owning service.</summary>
    public bool RequiresReconsent => MicrosoftFailureClassifier.RequiresReconsent(Kind);
}

/// <summary>
/// Microsoft refused the call (401/403 or a consent error). <see cref="MicrosoftFailureKind.CapabilityDenied"/>
/// when thrown directly: only one capability is affected and the tenant stays as it is.
/// </summary>
public class MicrosoftCallRefusedException : MicrosoftCallException
{
    public MicrosoftCallRefusedException()
    {
        Kind = MicrosoftFailureKind.CapabilityDenied;
    }

    public MicrosoftCallRefusedException(string message)
        : base(message)
    {
        Kind = MicrosoftFailureKind.CapabilityDenied;
    }

    public MicrosoftCallRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = MicrosoftFailureKind.CapabilityDenied;
    }

    public MicrosoftCallRefusedException(
        MicrosoftFailureKind kind,
        Guid tenantId,
        MicrosoftProvider provider,
        MicrosoftApp app,
        int? statusCode,
        string? errorCode,
        string message,
        Exception? innerException = null)
        : base(kind, tenantId, provider, app, statusCode, errorCode, message, innerException)
    {
    }
}

/// <summary>
/// The core grant is gone (<see cref="MicrosoftFailureKind.GrantRevoked"/>) or a Graph floor call
/// was refused (<see cref="MicrosoftFailureKind.FloorPermissionRemoved"/>). Retrying cannot help.
/// The tenant has <em>not</em> been flagged: the catcher that owns the tenant aggregate calls
/// <c>Tenant.MarkNeedsReconsent</c> on its own DbContext (ADR-016 rule 2).
/// </summary>
public class NeedsReconsentException : MicrosoftCallRefusedException
{
    public NeedsReconsentException()
        : base("Microsoft rejected the credential for this tenant.")
    {
        Kind = MicrosoftFailureKind.GrantRevoked;
    }

    public NeedsReconsentException(string message)
        : base(message)
    {
        Kind = MicrosoftFailureKind.GrantRevoked;
    }

    public NeedsReconsentException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = MicrosoftFailureKind.GrantRevoked;
    }

    /// <summary>Compatibility constructor: 401 → GrantRevoked, anything else → FloorPermissionRemoved.</summary>
    public NeedsReconsentException(Guid tenantId, MicrosoftProvider provider, int statusCode)
        : base(
            statusCode == 401 ? MicrosoftFailureKind.GrantRevoked : MicrosoftFailureKind.FloorPermissionRemoved,
            tenantId,
            provider,
            MicrosoftApp.Core,
            statusCode,
            errorCode: null,
            $"Microsoft returned {statusCode} for provider {provider}. The tenant must re-consent or restore the missing permission.")
    {
    }

    public NeedsReconsentException(
        MicrosoftFailureKind kind,
        Guid tenantId,
        MicrosoftProvider provider,
        int? statusCode,
        string? errorCode,
        string message,
        Exception? innerException = null)
        : base(kind, tenantId, provider, MicrosoftApp.Core, statusCode, errorCode, message, innerException)
    {
        if (!MicrosoftFailureClassifier.RequiresReconsent(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only GrantRevoked and FloorPermissionRemoved require re-consent.");
        }
    }

    /// <summary>The reason string to record on the tenant, for example <c>GrantRevoked:Graph:AADSTS700016</c>.</summary>
    public string Reason => string.Join(
        ':',
        new[] { Kind.ToString(), Provider.ToString(), ErrorCode ?? StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Where(part => !string.IsNullOrEmpty(part)));
}

/// <summary>A transient failure that outlived the retry budget, or an open circuit. Retry later.</summary>
public class MicrosoftTransientException : MicrosoftCallException
{
    public MicrosoftTransientException()
    {
        Kind = MicrosoftFailureKind.Transient;
    }

    public MicrosoftTransientException(string message)
        : base(message)
    {
        Kind = MicrosoftFailureKind.Transient;
    }

    public MicrosoftTransientException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = MicrosoftFailureKind.Transient;
    }

    public MicrosoftTransientException(
        Guid tenantId,
        MicrosoftProvider provider,
        MicrosoftApp app,
        int? statusCode,
        string? errorCode,
        TimeSpan? retryAfter,
        string message,
        Exception? innerException = null)
        : base(MicrosoftFailureKind.Transient, tenantId, provider, app, statusCode, errorCode, message, innerException)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>When a retry is worthwhile, if Microsoft or the breaker said.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// Microsoft asked us to wait longer than this host's budget allows
/// (<see cref="MicrosoftApiResilienceOptions.MaxHintedDelayInteractive"/> /
/// <see cref="MicrosoftApiResilienceOptions.MaxHintedDelayWorker"/>), or kept throttling after
/// every retry. Workers complete the message and re-enqueue it at <c>now + RetryAfter</c> (ADR-016 rule 7).
/// </summary>
public class MicrosoftThrottledException : MicrosoftTransientException, IRetryLaterFailure
{
    /// <summary>Used when Microsoft throttled without a usable hint.</summary>
    public static TimeSpan DefaultRetryAfter { get; } = TimeSpan.FromMinutes(1);

    public MicrosoftThrottledException()
    {
    }

    public MicrosoftThrottledException(string message)
        : base(message)
    {
    }

    public MicrosoftThrottledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MicrosoftThrottledException(TimeSpan retryAfter)
        : base($"Microsoft throttled the call; retry after {retryAfter}.")
    {
        base.RetryAfter = retryAfter;
    }

    public MicrosoftThrottledException(
        Guid tenantId,
        MicrosoftProvider provider,
        MicrosoftApp app,
        int? statusCode,
        TimeSpan retryAfter,
        string message)
        : base(tenantId, provider, app, statusCode, errorCode: null, retryAfter, message)
    {
    }

    /// <summary>When to re-queue the job. Never null for this type (<see cref="IRetryLaterFailure"/>).</summary>
    public new TimeSpan RetryAfter => base.RetryAfter ?? DefaultRetryAfter;
}

/// <summary>
/// MLCP's own credential failed or its app is paused (ADR-016 PlatformCredential). Nothing about
/// the tenant is concluded. Re-queue after <see cref="RetryAfter"/>.
/// </summary>
public class MicrosoftPlatformCredentialException : MicrosoftCallException, IRetryLaterFailure
{
    public MicrosoftPlatformCredentialException()
    {
        Kind = MicrosoftFailureKind.PlatformCredential;
    }

    public MicrosoftPlatformCredentialException(string message)
        : base(message)
    {
        Kind = MicrosoftFailureKind.PlatformCredential;
    }

    public MicrosoftPlatformCredentialException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = MicrosoftFailureKind.PlatformCredential;
    }

    public MicrosoftPlatformCredentialException(
        Guid tenantId,
        MicrosoftProvider provider,
        MicrosoftApp app,
        string? errorCode,
        TimeSpan retryAfter,
        string message,
        Exception? innerException = null)
        : base(MicrosoftFailureKind.PlatformCredential, tenantId, provider, app, null, errorCode, message, innerException)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(5);
}
