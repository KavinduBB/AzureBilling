using Mlcp.Domain.Sync;

namespace Mlcp.Application.Sync;

/// <summary>What the scheduler or a job handler asks the pipeline to run.</summary>
/// <param name="TenantId">The tenant being synced.</param>
/// <param name="JobType">The job; must match the handler's.</param>
/// <param name="CorrelationId">Ties logs, outbound calls and the run record together.</param>
/// <param name="RequestedMode">
/// Null for the job's default mode (<see cref="SyncJobTypeExtensions.LoadMode"/>). The
/// scheduler requests <see cref="SyncLoadMode.Full"/> for, say, the 30-day full resync of
/// <see cref="SyncJobType.UserAssignmentSync"/> (ADR-024 §2).
/// </param>
/// <param name="PeriodKey">
/// The billing period the run's data belongs to, or null for jobs that are not period-scoped.
/// Baselines, resume candidates and invoice exemptions are all matched on it (ADR-024 §3).
/// </param>
public sealed record SyncRunRequest(
    Guid TenantId,
    SyncJobType JobType,
    string CorrelationId,
    SyncLoadMode? RequestedMode = null,
    string? PeriodKey = null)
{
    /// <summary>The mode the run will use.</summary>
    public SyncLoadMode EffectiveMode => RequestedMode ?? JobType.LoadMode();
}

/// <summary>
/// Lets a handler persist its provider cursor as it goes, so an interrupted run can be resumed
/// by the next one (ADR-024 §8).
/// </summary>
public interface ISyncRunProgress
{
    /// <summary>
    /// Records <paramref name="continuationToken"/> as the point to resume from. Call it after
    /// each page has been durably written to staging, never before: a resumed run starts at the
    /// token and would otherwise skip the page.
    /// </summary>
    /// <remarks>
    /// Writing a page and recording its token are two operations, so a crash between them
    /// re-fetches that page on resume. Staging writes must therefore be idempotent on the
    /// natural key within a run (a MERGE into staging, or a unique index), or the resumed run
    /// stages duplicates.
    /// </remarks>
    Task CheckpointAsync(string continuationToken, CancellationToken cancellationToken);
}

/// <summary>Everything a job handler needs to know about the run it is executing.</summary>
/// <param name="TenantId">The tenant being synced.</param>
/// <param name="SyncRunId">
/// Tags every staged row, in a <c>SyncRunId</c> column of a <c>staging_*</c> table, so a run
/// can be isolated, resumed and swept. A resumed run's adopted rows have already been re-tagged
/// to this id.
/// </param>
/// <param name="JobType">The job being run.</param>
/// <param name="CorrelationId">Ties logs, outbound calls and the run record together.</param>
/// <param name="ContinuationToken">
/// Cursor from the failed or abandoned run this run adopted, or null to start from the
/// beginning. When non-null, staging already holds every page before this cursor.
/// </param>
/// <param name="Progress">Where to checkpoint the cursor after each staged page.</param>
public sealed record SyncRunContext(
    Guid TenantId,
    Guid SyncRunId,
    SyncJobType JobType,
    string CorrelationId,
    string? ContinuationToken,
    ISyncRunProgress Progress)
{
    /// <summary>The mode this run uses. A handler serving both modes branches on it.</summary>
    public SyncLoadMode LoadMode { get; init; } = JobType.LoadMode();

    /// <summary>The billing period, or null.</summary>
    public string? PeriodKey { get; init; }

    /// <summary>True when the run adopted an earlier run's staging.</summary>
    public bool IsResumed => ContinuationToken is not null;
}

/// <summary>What a fetch put into staging, and what the gate needs to judge it.</summary>
/// <param name="StagedRowCount">
/// Every row now in staging for <see cref="SyncRunContext.SyncRunId"/>, including rows adopted
/// from a resumed run — not just the rows this invocation fetched. This becomes the run's
/// <see cref="SyncRun.StagedRowCount"/> and the next run's baseline.
/// </param>
/// <param name="FieldViolations">
/// Required-field or range failures, one description per entry. A non-empty list fails the run
/// regardless of volume.
/// </param>
public sealed record StagingSummary(
    int StagedRowCount,
    IReadOnlyCollection<string> FieldViolations)
{
    public static StagingSummary Empty { get; } = new(0, []);

    public StagingSummary(int stagedRowCount)
        : this(stagedRowCount, [])
    {
    }
}

/// <summary>
/// One synchronisable dataset. Implementations fetch from Microsoft into a staging table and
/// know how to merge that staging table into their live table on the natural key.
/// </summary>
/// <remarks>
/// Handlers never write to live tables themselves and never decide whether their data is safe
/// to publish. <see cref="SyncPipeline"/> owns the run record, the lock, the validation gate
/// and the transaction, so those guarantees hold for every dataset rather than for whichever
/// handlers remembered (CLAUDE.md rule 6, ADR-024).
/// </remarks>
public interface ISyncJobHandler
{
    SyncJobType JobType { get; }

    /// <summary>
    /// Fetches from Microsoft and writes to staging, tagging every row with
    /// <see cref="SyncRunContext.SyncRunId"/>, starting from
    /// <see cref="SyncRunContext.ContinuationToken"/> when present and checkpointing through
    /// <see cref="SyncRunContext.Progress"/> after each page. Must not touch live tables.
    /// Returns only once every page has been staged.
    /// </summary>
    Task<StagingSummary> FetchToStagingAsync(SyncRunContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Merges this run's staged rows into the live table on the natural key, inside the
    /// transaction the pipeline has already opened. Returns rows affected.
    /// </summary>
    /// <remarks>
    /// Must use the scoped database context, so the statement enlists in that transaction, and
    /// must be idempotent: a transient failure retries the whole transaction, MERGE included
    /// (ADR-024 §6). Must not track entities in the change tracker; the store resets only the
    /// run record between attempts.
    /// </remarks>
    Task<int> MergeStagingToLiveAsync(SyncRunContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Removes this run's staged rows. Called after success, after a gate rejection, and after
    /// a failure that left nothing to resume from. Staging of a resumable failure is kept.
    /// </summary>
    Task ClearStagingAsync(SyncRunContext context, CancellationToken cancellationToken);
}

/// <summary>
/// An exclusive hold on one (tenant, job) pair for the duration of a run (ADR-024 §7).
/// Disposing it releases the hold.
/// </summary>
public interface ISyncRunLock : IAsyncDisposable
{
    /// <summary>The lock resource name, for logs.</summary>
    string Resource { get; }
}

/// <summary>Persistence of run records, kept behind an interface so Application holds no EF.</summary>
/// <remarks>
/// One instance per run, sharing the run's database context and connection: the run lock is a
/// session lock on that connection, and the merge transaction must run on it too.
/// </remarks>
public interface ISyncRunStore
{
    /// <summary>
    /// Tries to take the run lock without waiting. Null when another worker holds it.
    /// </summary>
    Task<ISyncRunLock?> TryAcquireLockAsync(Guid tenantId, SyncJobType jobType, CancellationToken cancellationToken);

    /// <summary>Persists a <see cref="SyncRunStatus.Skipped"/> run for a request that lost the lock.</summary>
    Task<SyncRun> RecordSkippedAsync(SyncRunRequest request, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// The latest failed or abandoned run of the same tenant, job, mode and period that has a
    /// cursor and was last updated at or after <paramref name="notBeforeUtc"/>; null when none.
    /// Call only while holding the run lock.
    /// </summary>
    Task<SyncRun?> FindResumableRunAsync(SyncRunRequest request, DateTimeOffset notBeforeUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Creates and persists a <see cref="SyncRunStatus.Running"/> run.
    /// </summary>
    /// <param name="request">What to run.</param>
    /// <param name="resumeFrom">
    /// A candidate from <see cref="FindResumableRunAsync"/>, or null. In one transaction the
    /// store re-tags the candidate's staging to the new run, records the adoption, and marks the
    /// candidate superseded. If the candidate has no staging rows left, it is not adopted and the
    /// new run starts from the beginning.
    /// </param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    Task<SyncRun> StartRunAsync(SyncRunRequest request, SyncRun? resumeFrom, CancellationToken cancellationToken);

    /// <summary>
    /// The last succeeded run of the same tenant, job, load mode and period, or null if there has
    /// never been one. The validation gate's baseline (ADR-024 §1–2).
    /// </summary>
    Task<SyncGateBaseline?> GetBaselineAsync(SyncRun run, CancellationToken cancellationToken);

    /// <summary>Saves pending changes to <paramref name="run"/>.</summary>
    Task SaveAsync(SyncRun run, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="operation"/> and saves <paramref name="run"/> in one database
    /// transaction, under the context's execution strategy (ADR-024 §5–6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation performs the MERGE and calls <see cref="SyncRun.Succeed"/>. Before every
    /// attempt the store resets <paramref name="run"/> to its last persisted values, so the
    /// operation always starts from a running run and may be re-executed whole.
    /// </para>
    /// <para>
    /// Returns normally only if the transaction committed, including when a commit whose outcome
    /// was lost is verified against the database afterwards; in that case the run is reloaded and
    /// the result is <c>default</c>. Throws only if nothing committed, and then
    /// <paramref name="run"/> is back in its pre-transaction state.
    /// </para>
    /// </remarks>
    Task<T> CompleteInTransactionAsync<T>(
        SyncRun run,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

/// <summary>Facts from other jobs the gate needs to judge a period-scoped drop (ADR-024 §3).</summary>
public interface ISyncGateContext
{
    /// <summary>
    /// True when <c>InvoiceSync</c> has recorded an invoice for <paramref name="periodKey"/> at
    /// or after <paramref name="sinceUtc"/>.
    /// </summary>
    Task<bool> InvoiceIssuedSinceAsync(
        Guid tenantId,
        string periodKey,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);
}

/// <summary>Operator overrides of the volume check (ADR-024 §4).</summary>
public interface ISyncGateOverrideStore
{
    /// <summary>Records an operator approval.</summary>
    Task AddAsync(SyncGateOverride approval, CancellationToken cancellationToken);

    /// <summary>
    /// The oldest unconsumed, unexpired override for this tenant and job, or null. Read-only:
    /// finding an override does not consume it.
    /// </summary>
    Task<SyncGateOverride?> FindAvailableAsync(
        Guid tenantId,
        SyncJobType jobType,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the override consumed by <paramref name="syncRunId"/> if it is still unconsumed and
    /// unexpired. Atomic, and enlists in the caller's transaction, so it rolls back with the
    /// merge. False when another run got there first or it has expired.
    /// </summary>
    Task<bool> TryConsumeAsync(
        Guid syncGateOverrideId,
        Guid syncRunId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// Generic access to the <c>staging_*</c> tables, independent of any one dataset.
/// </summary>
/// <remarks>
/// A staging table is any table whose name starts with <c>staging_</c> and that has a
/// <c>SyncRunId</c> column. Tables are discovered at run time, so a new dataset needs no
/// registration here, and a database with no staging tables yet is a normal case.
/// </remarks>
public interface IStagingStore
{
    /// <summary>True when any staging table holds a row tagged <paramref name="syncRunId"/>.</summary>
    Task<bool> HasRowsAsync(Guid syncRunId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-tags every staged row of <paramref name="fromSyncRunId"/> to
    /// <paramref name="toSyncRunId"/>. Enlists in the caller's transaction. Returns rows moved.
    /// </summary>
    Task<int> RetagAsync(Guid fromSyncRunId, Guid toSyncRunId, CancellationToken cancellationToken);

    /// <summary>Deletes every staged row of <paramref name="syncRunId"/>. Returns rows deleted.</summary>
    Task<int> DeleteForRunAsync(Guid syncRunId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes staged rows no longer wanted: those whose run no longer exists, or whose run is
    /// not running and was last updated before <paramref name="olderThanUtc"/>.
    /// </summary>
    Task<int> SweepAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);
}

/// <summary>Cross-tenant housekeeping for the sync worker.</summary>
public interface ISyncMaintenanceStore
{
    /// <summary>
    /// Marks <see cref="SyncRunStatus.Abandoned"/> every run left running longer than twice its
    /// job's <see cref="SyncJobTypeExtensions.MaxDuration"/>. Returns the number abandoned.
    /// </summary>
    Task<int> AbandonStuckRunsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes staging no run can resume any more (ADR-024 §8). Returns rows deleted.
    /// </summary>
    Task<int> SweepStagingAsync(CancellationToken cancellationToken);
}

/// <summary>The pipeline's verdict on one run.</summary>
/// <param name="Run">The run record, in its final state.</param>
/// <param name="LivePublished">
/// True only when the merge committed. False for a skip, a validation failure, an error, or a
/// lost consent — in all of which the live tables are exactly as they were.
/// </param>
public sealed record SyncRunOutcome(SyncRun Run, bool LivePublished)
{
    public SyncRunStatus Status => Run.Status;

    /// <summary>
    /// True when the run stopped because the tenant's floor-level grant is gone. The pipeline
    /// does not touch the tenant aggregate; the caller, which owns it, decides whether to flag
    /// the tenant <c>NeedsReconsent</c> (ADR-016 rule 2).
    /// </summary>
    public bool RequiresReconsent { get; init; }

    /// <summary>Why re-consent is needed, for the tenant record and the checklist.</summary>
    public string? ReconsentReason { get; init; }

    /// <summary>
    /// Set when Microsoft throttled the run for longer than the in-process budget. The caller
    /// should re-enqueue the job no earlier than this far in the future.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>True when a later run can adopt this run's staging and cursor.</summary>
    public bool IsResumable => Run.IsResumable;
}
