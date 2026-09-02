using Mlcp.Domain.Sync;

namespace Mlcp.Application.Sync;

/// <summary>Everything a job handler needs to know about the run it is executing.</summary>
/// <param name="TenantId">The tenant being synced.</param>
/// <param name="SyncRunId">Tags every staged row so a run can be isolated and rolled back.</param>
/// <param name="JobType">The job being run.</param>
/// <param name="CorrelationId">Ties logs, outbound calls and the run record together.</param>
/// <param name="ContinuationToken">
/// Cursor from an interrupted previous attempt, or null to start from the beginning.
/// </param>
public sealed record SyncRunContext(
    Guid TenantId,
    Guid SyncRunId,
    SyncJobType JobType,
    string CorrelationId,
    string? ContinuationToken);

/// <summary>What a fetch put into staging, and what the gate needs to judge it.</summary>
/// <param name="StagedRowCount">Rows written to the staging table for this run.</param>
/// <param name="FieldViolations">
/// Required-field or range failures, one description per entry. A non-empty list fails the run
/// regardless of volume.
/// </param>
/// <param name="ContinuationToken">
/// Non-null when the provider has more pages. The pipeline persists it so an interrupted run
/// resumes rather than restarting a large fetch.
/// </param>
public sealed record StagingSummary(
    int StagedRowCount,
    IReadOnlyCollection<string> FieldViolations,
    string? ContinuationToken = null)
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
/// to publish. <see cref="SyncPipeline"/> owns the run record, the validation gate and the
/// transaction, so those guarantees hold for every dataset rather than for whichever handlers
/// remembered (CLAUDE.md rule 6).
/// </remarks>
public interface ISyncJobHandler
{
    SyncJobType JobType { get; }

    /// <summary>
    /// Fetches from Microsoft and writes to staging, tagging every row with
    /// <see cref="SyncRunContext.SyncRunId"/>. Must not touch live tables.
    /// </summary>
    Task<StagingSummary> FetchToStagingAsync(SyncRunContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Merges this run's staged rows into the live table on the natural key, inside the
    /// transaction the pipeline has already opened. Returns rows affected.
    /// </summary>
    Task<int> MergeStagingToLiveAsync(SyncRunContext context, CancellationToken cancellationToken);

    /// <summary>Removes this run's staged rows. Called after success and after failure alike.</summary>
    Task ClearStagingAsync(SyncRunContext context, CancellationToken cancellationToken);
}

/// <summary>Persistence of run records, kept behind an interface so Application holds no EF.</summary>
public interface ISyncRunStore
{
    Task<SyncRun> StartRunAsync(Guid tenantId, SyncJobType jobType, string correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// Rows processed by the last successful run of this job for this tenant, or null if there
    /// has never been one. The validation gate's baseline.
    /// </summary>
    Task<int?> GetLastSuccessfulRecordCountAsync(Guid tenantId, SyncJobType jobType, CancellationToken cancellationToken);

    Task SaveAsync(SyncRun run, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="action"/> inside a single database transaction.</summary>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}

/// <summary>The pipeline's verdict on one run.</summary>
/// <param name="Run">The persisted run record, in its final state.</param>
/// <param name="LivePublished">
/// True only when the merge ran. False for a validation failure, an error, or a lost consent —
/// in all of which the live tables are exactly as they were.
/// </param>
public sealed record SyncRunOutcome(SyncRun Run, bool LivePublished)
{
    public SyncRunStatus Status => Run.Status;
}
