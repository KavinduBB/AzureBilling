namespace Mlcp.Shared.Resilience;

/// <summary>
/// Implemented by an exception meaning "Microsoft asked us to come back later, and waiting
/// in-process is too long" (ADR-016 rule 7).
/// </summary>
/// <remarks>
/// The sync pipeline reacts to this contract rather than to concrete exception types: it records
/// the run as failed (keeping staging and the cursor for resume) and reports
/// <see cref="RetryAfter"/> so the caller re-enqueues with a scheduled time instead of sleeping.
/// </remarks>
public interface IRetryLaterFailure
{
    /// <summary>How long to wait before trying again.</summary>
    TimeSpan RetryAfter { get; }
}
