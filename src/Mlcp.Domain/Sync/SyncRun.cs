using Mlcp.Domain.Common;

namespace Mlcp.Domain.Sync;

/// <remarks>
/// Stored by name. New members are appended; existing ones are never renumbered or renamed,
/// because run history outlives the code that wrote it.
/// </remarks>
public enum SyncRunStatus
{
    Unknown = 0,
    Running = 1,
    Succeeded = 2,

    /// <summary>The run failed. Live tables were not touched. May be resumed (ADR-024 §8).</summary>
    Failed = 3,

    /// <summary>The validation gate rejected the staged data. Live tables were not touched.</summary>
    ValidationFailed = 4,

    /// <summary>Aborted because the tenant lost consent mid-run.</summary>
    AbortedNeedsReconsent = 5,

    /// <summary>
    /// Another worker already held the run lock for this tenant and job, so this run did
    /// nothing (ADR-024 §7). Recorded rather than dropped so the sync health page can show it.
    /// </summary>
    Skipped = 6,

    /// <summary>
    /// Left <see cref="Running"/> for longer than twice the job's maximum duration; its worker
    /// is presumed dead. May be resumed (ADR-024 §7–8).
    /// </summary>
    Abandoned = 7,

    /// <summary>
    /// A failed or abandoned run whose staging and continuation token were adopted by
    /// <see cref="SyncRun.SupersededBySyncRunId"/>. Never resumed again.
    /// </summary>
    Superseded = 8,
}

/// <summary>
/// One execution of one sync job for one tenant. Also the freshness stamp: every figure a
/// dashboard renders traces back to the <see cref="CompletedUtc"/> of the run that wrote it,
/// which is what the "as of" labels display (docs/03-architecture.md §7).
/// </summary>
/// <remarks>
/// <para>
/// The status is a one-way state machine. <see cref="SyncRunStatus.Running"/> is the only
/// state that can move to a terminal state, and terminal states are final with one exception:
/// a <see cref="SyncRunStatus.Failed"/> or <see cref="SyncRunStatus.Abandoned"/> run can be
/// marked <see cref="SyncRunStatus.Superseded"/> once, when a later run adopts its staging.
/// Every other transition throws. A run that silently flipped from Succeeded to Failed after
/// its merge committed would tell an operator the live tables were untouched when they were
/// not, which is the exact confusion ADR-024 §5 removes.
/// </para>
/// <para>
/// Staged rows are always tagged with <see cref="SyncRunId"/>. A resumed run re-tags the rows
/// of the run it adopts to its own id, inside the transaction that marks the old run
/// superseded, so a worker that was presumed dead but is still writing can only add rows under
/// the old id, which nothing will ever merge and the staging sweeper removes.
/// </para>
/// </remarks>
public class SyncRun : TenantEntity
{
    public Guid SyncRunId { get; private set; }

    public SyncJobType JobType { get; private set; }

    public SyncRunStatus Status { get; private set; }

    /// <summary>
    /// The load mode this run used. Chosen per run (ADR-024 §2): the job's default unless the
    /// scheduler requested otherwise. Baselines only compare runs of the same mode.
    /// </summary>
    public SyncLoadMode LoadMode { get; private set; }

    /// <summary>
    /// The billing period the run's data belongs to (for example <c>2026-09</c>), or null for
    /// jobs that are not period-scoped. Baselines only compare runs of the same period, so the
    /// first run of a new period has no volume check (ADR-024 §3).
    /// </summary>
    public string? PeriodKey { get; private set; }

    public DateTimeOffset StartedUtc { get; private set; }

    public DateTimeOffset? CompletedUtc { get; private set; }

    /// <summary>
    /// Rows the MERGE affected. Telemetry only: a quiet run affects few rows even when it staged
    /// the full set, so this is never used as the validation gate's baseline (ADR-024 §1).
    /// </summary>
    public int RecordsProcessed { get; private set; }

    /// <summary>
    /// Every row in staging for this run when the gate judged it, including rows adopted from a
    /// resumed run. The only baseline the gate uses (ADR-024 §1). Null until the gate runs.
    /// </summary>
    public int? StagedRowCount { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Ties this run to the log entries and outbound Microsoft calls it produced.</summary>
    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>
    /// Provider paging cursor, persisted after each staged page so an interrupted run can be
    /// resumed rather than restarted. Delta links can embed identifiers, so this column is
    /// redacted from logs. Cleared once the run succeeds or its staging is discarded.
    /// </summary>
    public string? ContinuationToken { get; private set; }

    /// <summary>
    /// Human-readable outcome of the validation gate, kept for both passes and failures. Also
    /// the audit trail for an operator override: the override id and approver are written here.
    /// </summary>
    public string? ValidationNotes { get; private set; }

    /// <summary>The failed or abandoned run whose staging this run adopted, if any.</summary>
    public Guid? ResumedFromSyncRunId { get; private set; }

    /// <summary>The run that adopted this run's staging. Set only with <see cref="SyncRunStatus.Superseded"/>.</summary>
    public Guid? SupersededBySyncRunId { get; private set; }

    private SyncRun()
    {
    }

    private SyncRun(Guid tenantId, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
    }

    /// <summary>True once the run can no longer change, other than being superseded.</summary>
    public bool IsTerminal => Status is not (SyncRunStatus.Running or SyncRunStatus.Unknown);

    /// <summary>True when this run's data may be published as the tenant's current position.</summary>
    public bool IsSuccessful => Status == SyncRunStatus.Succeeded;

    /// <summary>
    /// True when a later run may adopt this run's staging and continue from its cursor
    /// (ADR-024 §8). The staging rows themselves are checked by the store.
    /// </summary>
    public bool IsResumable =>
        Status is SyncRunStatus.Failed or SyncRunStatus.Abandoned
        && !string.IsNullOrEmpty(ContinuationToken);

    public static SyncRun Start(Guid tenantId, SyncJobType jobType, string correlationId, DateTimeOffset nowUtc)
        => Start(tenantId, jobType, correlationId, nowUtc, loadMode: null, periodKey: null);

    /// <param name="tenantId">The tenant being synced.</param>
    /// <param name="jobType">A concrete job type.</param>
    /// <param name="correlationId">Ties the run to its logs.</param>
    /// <param name="nowUtc">Start time.</param>
    /// <param name="loadMode">The requested mode, or null for the job's default.</param>
    /// <param name="periodKey">The billing period, or null for jobs that are not period-scoped.</param>
    public static SyncRun Start(
        Guid tenantId,
        SyncJobType jobType,
        string correlationId,
        DateTimeOffset nowUtc,
        SyncLoadMode? loadMode,
        string? periodKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (jobType == SyncJobType.Unknown)
        {
            throw new ArgumentException("A sync run must name a concrete job type.", nameof(jobType));
        }

        var mode = loadMode ?? jobType.LoadMode();

        if (mode == SyncLoadMode.Unknown)
        {
            throw new ArgumentException("A sync run must use a concrete load mode.", nameof(loadMode));
        }

        if (periodKey is not null && string.IsNullOrWhiteSpace(periodKey))
        {
            // An empty key would be a distinct baseline from null and from every real period,
            // so a typo would silently give every run a first-run pass.
            throw new ArgumentException("A period key must be null or non-blank.", nameof(periodKey));
        }

        return new SyncRun(tenantId, nowUtc)
        {
            SyncRunId = Guid.NewGuid(),
            JobType = jobType,
            LoadMode = mode,
            PeriodKey = periodKey,
            Status = SyncRunStatus.Running,
            StartedUtc = nowUtc,
            CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// Records a run that did not execute because another worker held the lock for this tenant
    /// and job (ADR-024 §7). It starts and ends in the same instant.
    /// </summary>
    public static SyncRun RecordSkipped(
        Guid tenantId,
        SyncJobType jobType,
        string correlationId,
        SyncLoadMode? loadMode,
        string? periodKey,
        string reason,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var run = Start(tenantId, jobType, correlationId, nowUtc, loadMode, periodKey);
        run.Status = SyncRunStatus.Skipped;
        run.ErrorCode = "LockHeld";
        run.ErrorMessage = reason;
        run.CompletedUtc = nowUtc;
        return run;
    }

    /// <summary>
    /// Takes over a failed or abandoned run's cursor (ADR-024 §8). The caller re-tags the old
    /// run's staging to this run and marks it superseded in the same transaction.
    /// </summary>
    public void ResumeFrom(SyncRun previous, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(previous);
        EnsureRunning(nameof(ResumeFrom));

        if (previous.SyncRunId == SyncRunId)
        {
            throw new DomainException("A run cannot resume from itself.");
        }

        if (ResumedFromSyncRunId is not null)
        {
            throw new DomainException($"Run {SyncRunId} has already adopted run {ResumedFromSyncRunId}.");
        }

        if (!previous.IsResumable)
        {
            throw new DomainException(
                $"Run {previous.SyncRunId} is {previous.Status} with {(previous.ContinuationToken is null ? "no" : "a")} cursor and cannot be resumed.");
        }

        if (previous.TenantId != TenantId
            || previous.JobType != JobType
            || previous.LoadMode != LoadMode
            || !string.Equals(previous.PeriodKey, PeriodKey, StringComparison.Ordinal))
        {
            // Staging from a different tenant, job, mode or period is a different dataset.
            throw new DomainException(
                $"Run {previous.SyncRunId} ({previous.JobType}/{previous.LoadMode}/{previous.PeriodKey}) does not match "
                + $"run {SyncRunId} ({JobType}/{LoadMode}/{PeriodKey}).");
        }

        ResumedFromSyncRunId = previous.SyncRunId;
        ContinuationToken = previous.ContinuationToken;
        Touch(nowUtc);
    }

    /// <summary>
    /// Persists the provider cursor after a staged page so a crash resumes from here. Also acts
    /// as a heartbeat through <c>UpdatedUtc</c>.
    /// </summary>
    public void RecordContinuation(string? continuationToken, DateTimeOffset nowUtc)
    {
        EnsureRunning(nameof(RecordContinuation));
        ContinuationToken = continuationToken;
        Touch(nowUtc);
    }

    /// <summary>Records the total staged for this run, ahead of the gate.</summary>
    public void RecordStaged(int stagedRowCount, DateTimeOffset nowUtc)
    {
        EnsureRunning(nameof(RecordStaged));
        ArgumentOutOfRangeException.ThrowIfNegative(stagedRowCount);
        StagedRowCount = stagedRowCount;
        Touch(nowUtc);
    }

    /// <summary>
    /// Marks the run succeeded. Must be called inside the transaction that ran the MERGE
    /// (ADR-024 §5), so the status and the live data commit or roll back together.
    /// </summary>
    public void Succeed(int recordsMerged, string? validationNotes, DateTimeOffset nowUtc)
    {
        EnsureRunning(nameof(Succeed));
        ArgumentOutOfRangeException.ThrowIfNegative(recordsMerged);

        if (StagedRowCount is null)
        {
            // Without a staged count this run could never serve as a baseline, and the next run
            // would get a first-run pass it did not earn.
            throw new DomainException($"Run {SyncRunId} cannot succeed before its staged row count is recorded.");
        }

        Status = SyncRunStatus.Succeeded;
        RecordsProcessed = recordsMerged;
        ValidationNotes = validationNotes;
        CompletedUtc = nowUtc;
        ContinuationToken = null;
        Touch(nowUtc);
    }

    /// <summary>
    /// Marks the run failed. The continuation token is kept, so a later run can resume from it.
    /// </summary>
    public void Fail(string errorCode, string errorMessage, DateTimeOffset nowUtc)
    {
        EnsureRunning(nameof(Fail));
        Status = SyncRunStatus.Failed;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CompletedUtc = nowUtc;
        Touch(nowUtc);
    }

    /// <summary>Marks the run rejected by the gate. Its staging is discarded, so its cursor is too.</summary>
    public void FailValidation(string validationNotes, DateTimeOffset nowUtc)
    {
        EnsureRunning(nameof(FailValidation));
        Status = SyncRunStatus.ValidationFailed;
        ValidationNotes = validationNotes;
        ErrorCode = "ValidationGate";
        CompletedUtc = nowUtc;
        ContinuationToken = null;
        Touch(nowUtc);
    }

    public void AbortNeedsReconsent(string detail, DateTimeOffset nowUtc)
    {
        EnsureRunning(nameof(AbortNeedsReconsent));
        Status = SyncRunStatus.AbortedNeedsReconsent;
        ErrorCode = "NeedsReconsent";
        ErrorMessage = detail;
        CompletedUtc = nowUtc;
        ContinuationToken = null;
        Touch(nowUtc);
    }

    /// <summary>
    /// Marks a run whose worker is presumed dead. The cursor is kept so the run can be resumed.
    /// </summary>
    public void Abandon(string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureRunning(nameof(Abandon));
        Status = SyncRunStatus.Abandoned;
        ErrorCode = "Abandoned";
        ErrorMessage = reason;
        CompletedUtc = nowUtc;
        Touch(nowUtc);
    }

    /// <summary>
    /// Records that <paramref name="successorSyncRunId"/> adopted this run's staging and cursor.
    /// The one transition permitted out of a terminal state.
    /// </summary>
    public void Supersede(Guid successorSyncRunId, DateTimeOffset nowUtc)
    {
        if (!IsResumable)
        {
            throw new DomainException(
                $"Run {SyncRunId} is {Status} and cannot be superseded; only a failed or abandoned run with a cursor can.");
        }

        if (successorSyncRunId == Guid.Empty || successorSyncRunId == SyncRunId)
        {
            throw new DomainException("A run must be superseded by a different run.");
        }

        Status = SyncRunStatus.Superseded;
        SupersededBySyncRunId = successorSyncRunId;
        ContinuationToken = null;
        Touch(nowUtc);
    }

    private void EnsureRunning(string operation)
    {
        if (Status != SyncRunStatus.Running)
        {
            throw new DomainException(
                $"Cannot {operation} sync run {SyncRunId}: it is {Status}, and only a running run can change.");
        }
    }
}
