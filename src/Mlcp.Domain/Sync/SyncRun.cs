using Mlcp.Domain.Common;

namespace Mlcp.Domain.Sync;

public enum SyncRunStatus
{
    Unknown = 0,
    Running = 1,
    Succeeded = 2,

    /// <summary>The run failed. Live tables were not touched.</summary>
    Failed = 3,

    /// <summary>The validation gate rejected the staged data. Live tables were not touched.</summary>
    ValidationFailed = 4,

    /// <summary>Aborted because the tenant lost consent mid-run.</summary>
    AbortedNeedsReconsent = 5,
}

/// <summary>
/// One execution of one sync job for one tenant. Also the freshness stamp: every figure a
/// dashboard renders traces back to the <see cref="CompletedUtc"/> of the run that wrote it,
/// which is what the "as of" labels display (docs/03-architecture.md §7).
/// </summary>
public class SyncRun : TenantEntity
{
    public Guid SyncRunId { get; private set; }

    public SyncJobType JobType { get; private set; }

    public SyncRunStatus Status { get; private set; }

    public DateTimeOffset StartedUtc { get; private set; }

    public DateTimeOffset? CompletedUtc { get; private set; }

    public int RecordsProcessed { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Ties this run to the log entries and outbound Microsoft calls it produced.</summary>
    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>
    /// Provider paging cursor, persisted so an interrupted run resumes rather than restarts.
    /// Delta links can embed identifiers, so this column is redacted from logs.
    /// </summary>
    public string? ContinuationToken { get; private set; }

    /// <summary>Human-readable outcome of the validation gate, kept for both passes and failures.</summary>
    public string? ValidationNotes { get; private set; }

    private SyncRun()
    {
    }

    private SyncRun(Guid tenantId, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
    }

    public static SyncRun Start(Guid tenantId, SyncJobType jobType, string correlationId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (jobType == SyncJobType.Unknown)
        {
            throw new ArgumentException("A sync run must name a concrete job type.", nameof(jobType));
        }

        return new SyncRun(tenantId, nowUtc)
        {
            SyncRunId = Guid.NewGuid(),
            JobType = jobType,
            Status = SyncRunStatus.Running,
            StartedUtc = nowUtc,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Persists the provider cursor mid-run so a crash resumes from here.</summary>
    public void RecordContinuation(string? continuationToken, int recordsProcessedSoFar, DateTimeOffset nowUtc)
    {
        ContinuationToken = continuationToken;
        RecordsProcessed = recordsProcessedSoFar;
        Touch(nowUtc);
    }

    public void Succeed(int recordsProcessed, string? validationNotes, DateTimeOffset nowUtc)
    {
        Status = SyncRunStatus.Succeeded;
        RecordsProcessed = recordsProcessed;
        ValidationNotes = validationNotes;
        CompletedUtc = nowUtc;
        ContinuationToken = null;
        Touch(nowUtc);
    }

    public void Fail(string errorCode, string errorMessage, DateTimeOffset nowUtc)
    {
        Status = SyncRunStatus.Failed;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CompletedUtc = nowUtc;
        Touch(nowUtc);
    }

    public void FailValidation(string validationNotes, DateTimeOffset nowUtc)
    {
        Status = SyncRunStatus.ValidationFailed;
        ValidationNotes = validationNotes;
        ErrorCode = "ValidationGate";
        CompletedUtc = nowUtc;
        Touch(nowUtc);
    }

    public void AbortNeedsReconsent(string detail, DateTimeOffset nowUtc)
    {
        Status = SyncRunStatus.AbortedNeedsReconsent;
        ErrorCode = "NeedsReconsent";
        ErrorMessage = detail;
        CompletedUtc = nowUtc;
        Touch(nowUtc);
    }

    /// <summary>True when this run's data may be published as the tenant's current position.</summary>
    public bool IsSuccessful => Status == SyncRunStatus.Succeeded;
}
