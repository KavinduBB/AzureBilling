using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mlcp.Application.Sync;
using Mlcp.Domain.Sync;

namespace Mlcp.Persistence.Stores;

/// <summary>
/// EF-backed store for sync run records, the run lock and the pipeline's transaction boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock.</b> <c>sp_getapplock</c> with <c>@LockOwner = 'Session'</c> ties the lock to one SQL
/// session, so the store opens the context's connection when the lock is taken and keeps it
/// open until the lock is released; every query of the run, the merge transaction included,
/// runs on that same session. If the connection breaks the server drops the lock, which is why
/// the merge transaction re-checks it with <c>APPLOCK_MODE</c> before committing: a worker whose
/// lock silently vanished must not publish over a worker that has since taken it.
/// </para>
/// <para>
/// <b>Transactions.</b> Production contexts use <c>EnableRetryOnFailure</c>, whose execution
/// strategy refuses a user transaction opened outside it. Every transaction here therefore runs
/// inside <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c>, and each attempt starts by
/// resetting the tracked entities it touches to their last persisted values, so a retry repeats
/// the whole unit rather than continuing from a half-applied state (ADR-024 §6). Changes are
/// saved with <c>acceptAllChangesOnSuccess: false</c> and accepted only after the commit.
/// </para>
/// </remarks>
public sealed partial class SyncRunStore : ISyncRunStore
{
    private const string AcquireLockSql = """
        DECLARE @result int;
        EXEC @result = sys.sp_getapplock
            @Resource = @resource,
            @LockMode = N'Exclusive',
            @LockOwner = N'Session',
            @LockTimeout = 0,
            @DbPrincipal = N'public';
        SELECT @result AS [Value];
        """;

    private const string ReleaseLockSql = """
        EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session', @DbPrincipal = N'public';
        """;

    private const string LockModeSql = """
        SELECT APPLOCK_MODE(N'public', @resource, N'Session') AS [Value];
        """;

    private readonly MlcpDbContext _context;
    private readonly IStagingStore _staging;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncRunStore> _logger;

    private string? _heldLockResource;

    public SyncRunStore(
        MlcpDbContext context,
        IStagingStore staging,
        TimeProvider timeProvider,
        ILogger<SyncRunStore>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<SyncRunStore>.Instance;
    }

    /// <summary>The applock resource for a tenant and job: <c>sync:{tenant:D}:{job}</c>.</summary>
    public static string LockResource(Guid tenantId, SyncJobType jobType) => $"sync:{tenantId:D}:{jobType}";

    public async Task<ISyncRunLock?> TryAcquireLockAsync(
        Guid tenantId,
        SyncJobType jobType,
        CancellationToken cancellationToken)
    {
        if (_heldLockResource is not null)
        {
            throw new InvalidOperationException(
                $"This store already holds {_heldLockResource}. Use one store, and one context, per run.");
        }

        var resource = LockResource(tenantId, jobType);

        await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var results = await _context.Database
                .SqlQueryRaw<int>(AcquireLockSql, ResourceParameter(resource))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // 0 = granted, 1 = granted after waiting; negative = timeout, deadlock or error.
            if (results.Count == 1 && results[0] >= 0)
            {
                _heldLockResource = resource;
                return new RunLock(this, resource);
            }

            LogLockNotAcquired(resource, results.Count == 1 ? results[0] : int.MinValue);
        }
        catch
        {
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }

        await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        return null;
    }

    private async ValueTask ReleaseLockAsync(string resource)
    {
        try
        {
            if (_context.Database.CurrentTransaction is not null)
            {
                // Never expected: every transaction here is scoped to one call. Releasing a
                // session lock does not need one, and committing someone else's would be wrong.
                LogReleaseInsideTransaction(resource);
            }

            await _context.Database
                .ExecuteSqlRawAsync(ReleaseLockSql, [ResourceParameter(resource)], CancellationToken.None)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Release failure must not mask the run's outcome.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The session is probably broken, in which case the server has already dropped the
            // lock. Evict pooled connections so a live session holding it cannot be reused.
            LogReleaseFailed(ex, resource);

            if (_context.Database.GetDbConnection() is SqlConnection sqlConnection)
            {
                SqlConnection.ClearPool(sqlConnection);
            }
        }
        finally
        {
            _heldLockResource = null;
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    public async Task<SyncRun> RecordSkippedAsync(
        SyncRunRequest request,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var run = SyncRun.RecordSkipped(
            request.TenantId,
            request.JobType,
            request.CorrelationId,
            request.RequestedMode,
            request.PeriodKey,
            reason,
            _timeProvider.GetUtcNow());

        await _context.SyncRuns.AddAsync(run, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return run;
    }

    public async Task<SyncRun?> FindResumableRunAsync(
        SyncRunRequest request,
        DateTimeOffset notBeforeUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = request.TenantId;
        var jobType = request.JobType;
        var mode = request.EffectiveMode;
        var periodKey = request.PeriodKey;

        var candidate = await _context.SyncRuns
            .Where(r => r.TenantId == tenantId
                && r.JobType == jobType
                && r.LoadMode == mode
                && r.PeriodKey == periodKey
                && (r.Status == SyncRunStatus.Failed || r.Status == SyncRunStatus.Abandoned)
                && r.ContinuationToken != null
                && r.UpdatedUtc >= notBeforeUtc)
            .OrderByDescending(r => r.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidate is null)
        {
            return null;
        }

        // A cursor without its staged pages would resume mid-way and stage a partial set.
        return await _staging.HasRowsAsync(candidate.SyncRunId, cancellationToken).ConfigureAwait(false)
            ? candidate
            : null;
    }

    public async Task<SyncRun> StartRunAsync(
        SyncRunRequest request,
        SyncRun? resumeFrom,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfAmbientTransaction();

        var nowUtc = _timeProvider.GetUtcNow();

        if (resumeFrom is null)
        {
            var run = NewRun(request, nowUtc);
            await _context.SyncRuns.AddAsync(run, cancellationToken).ConfigureAwait(false);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return run;
        }

        var state = new AdoptionState(request, resumeFrom, nowUtc);
        var strategy = _context.Database.CreateExecutionStrategy();

        var adopted = await strategy
            .ExecuteAsync(
                state,
                (_, s, ct) => AttemptAdoptionAsync(s, ct),
                (_, s, ct) => VerifyAdoptionAsync(s, ct),
                cancellationToken)
            .ConfigureAwait(false);

        if (state.Verified)
        {
            // The commit's outcome was lost and then found in the database: make the tracked
            // entities match it.
            await _context.Entry(resumeFrom).ReloadAsync(CancellationToken.None).ConfigureAwait(false);
            await _context.Entry(adopted).ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return adopted;
    }

    /// <summary>
    /// Creates the run, re-tags the old run's staging to it, and supersedes the old run, all or
    /// nothing. If no rows move, the old staging is gone and the new run starts from scratch.
    /// </summary>
    /// <remarks>
    /// Saves are accepted as they go (the new run is inserted before its id tags any staging
    /// row, so the staging sweeper never sees rows tagged with an unknown run). A retry
    /// therefore cannot trust the tracker: it detaches the previous attempt's run and reloads
    /// the old run from the database, which the rollback has restored.
    /// </remarks>
    private async Task<SyncRun> AttemptAdoptionAsync(AdoptionState state, CancellationToken cancellationToken)
    {
        if (state.Run is { } previousAttempt)
        {
            _context.Entry(previousAttempt).State = EntityState.Detached;
            await _context.Entry(state.ResumeFrom).ReloadAsync(cancellationToken).ConfigureAwait(false);
        }

        var run = NewRun(state.Request, state.NowUtc);
        state.Run = run;

        var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (transaction.ConfigureAwait(false))
        {
            await _context.SyncRuns.AddAsync(run, cancellationToken).ConfigureAwait(false);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var moved = await _staging
                .RetagAsync(state.ResumeFrom.SyncRunId, run.SyncRunId, cancellationToken)
                .ConfigureAwait(false);

            if (moved > 0 && state.ResumeFrom.IsResumable)
            {
                run.ResumeFrom(state.ResumeFrom, state.NowUtc);
                state.ResumeFrom.Supersede(run.SyncRunId, state.NowUtc);
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                LogAdoptionFoundNoStaging(state.ResumeFrom.SyncRunId, run.SyncRunId);

                if (moved > 0)
                {
                    // The candidate stopped being resumable between lookup and adoption; give
                    // its rows back rather than merge rows this run has no cursor for.
                    await _staging.RetagAsync(run.SyncRunId, state.ResumeFrom.SyncRunId, cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return run;
    }

    private async Task<ExecutionResult<SyncRun>> VerifyAdoptionAsync(AdoptionState state, CancellationToken cancellationToken)
    {
        if (state.Run is not { } run || _context.Entry(run).State == EntityState.Added)
        {
            return new ExecutionResult<SyncRun>(false, null!);
        }

        var runId = run.SyncRunId;
        var exists = await _context.SyncRuns
            .AsNoTracking()
            .AnyAsync(r => r.SyncRunId == runId, cancellationToken)
            .ConfigureAwait(false);

        state.Verified = exists;
        return new ExecutionResult<SyncRun>(exists, run);
    }

    /// <summary>
    /// The most recent succeeded run of the same tenant, job, load mode and period.
    /// </summary>
    /// <remarks>
    /// Only <see cref="SyncRunStatus.Succeeded"/> runs count, and only their staged count. A
    /// failed or gate-rejected run never published its rows, and the merged count shrinks on a
    /// quiet run; using either would let a truncated response become the new normal, so the
    /// gate would ratchet itself down instead of holding a line (ADR-024 §1). Succeeded runs
    /// written before <see cref="SyncRun.StagedRowCount"/> existed have no staged count and are
    /// ignored, so the first run after that change is treated as a first run.
    /// </remarks>
    public async Task<SyncGateBaseline?> GetBaselineAsync(SyncRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var tenantId = run.TenantId;
        var jobType = run.JobType;
        var mode = run.LoadMode;
        var periodKey = run.PeriodKey;
        var runId = run.SyncRunId;

        var baseline = await _context.SyncRuns
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                && r.JobType == jobType
                && r.LoadMode == mode
                && r.PeriodKey == periodKey
                && r.Status == SyncRunStatus.Succeeded
                && r.StagedRowCount != null
                && r.SyncRunId != runId)
            .OrderByDescending(r => r.CompletedUtc)
            .ThenByDescending(r => r.StartedUtc)
            .Select(r => new { r.SyncRunId, r.StagedRowCount, r.StartedUtc })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return baseline is null
            ? null
            : new SyncGateBaseline(baseline.SyncRunId, baseline.StagedRowCount!.Value, baseline.StartedUtc);
    }

    public Task SaveAsync(SyncRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<T> CompleteInTransactionAsync<T>(
        SyncRun run,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfAmbientTransaction();

        if (_context.Entry(run).State == EntityState.Detached)
        {
            throw new InvalidOperationException($"Run {run.SyncRunId} is not tracked by this store's context.");
        }

        var state = new CompletionState<T>(run, operation);
        var strategy = _context.Database.CreateExecutionStrategy();
        T result;

        try
        {
            result = await strategy
                .ExecuteAsync(
                    state,
                    (_, s, ct) => AttemptCompletionAsync(s, ct),
                    (_, s, ct) => VerifyCompletionAsync(s, ct),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (state.CommitAttempted)
        {
            // The commit itself failed and was not retried (for example a non-retrying strategy,
            // or retries exhausted). Its outcome is unknown until the database says otherwise.
            if (await IsSucceededInDatabaseAsync(run.SyncRunId, CancellationToken.None).ConfigureAwait(false))
            {
                LogCommitVerified(ex, run.SyncRunId);
                await _context.Entry(run).ReloadAsync(CancellationToken.None).ConfigureAwait(false);
                return default!;
            }

            ResetToPersisted(run);
            throw;
        }
        catch
        {
            ResetToPersisted(run);
            throw;
        }

        if (state.Verified)
        {
            await _context.Entry(run).ReloadAsync(CancellationToken.None).ConfigureAwait(false);
            return default!;
        }

        _context.ChangeTracker.AcceptAllChanges();
        return result;
    }

    private async Task<T> AttemptCompletionAsync<T>(CompletionState<T> state, CancellationToken cancellationToken)
    {
        state.CommitAttempted = false;
        ResetToPersisted(state.Run);

        var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (transaction.ConfigureAwait(false))
        {
            await EnsureLockStillHeldAsync(cancellationToken).ConfigureAwait(false);

            var result = await state.Operation(cancellationToken).ConfigureAwait(false);

            await _context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken).ConfigureAwait(false);

            // Once the commit is on the wire, cancelling it would only make its outcome unknown.
            state.CommitAttempted = true;
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);

            return result;
        }
    }

    private async Task<ExecutionResult<T>> VerifyCompletionAsync<T>(CompletionState<T> state, CancellationToken cancellationToken)
    {
        // Only a failed commit can have committed; any earlier failure rolled back.
        if (!state.CommitAttempted)
        {
            return new ExecutionResult<T>(false, default!);
        }

        var succeeded = await IsSucceededInDatabaseAsync(state.Run.SyncRunId, cancellationToken).ConfigureAwait(false);
        state.Verified = succeeded;
        return new ExecutionResult<T>(succeeded, default!);
    }

    private Task<bool> IsSucceededInDatabaseAsync(Guid syncRunId, CancellationToken cancellationToken)
        => _context.SyncRuns
            .AsNoTracking()
            .AnyAsync(r => r.SyncRunId == syncRunId && r.Status == SyncRunStatus.Succeeded, cancellationToken);

    private async Task EnsureLockStillHeldAsync(CancellationToken cancellationToken)
    {
        if (_heldLockResource is not { } resource)
        {
            return;
        }

        var modes = await _context.Database
            .SqlQueryRaw<string>(LockModeSql, ResourceParameter(resource))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (modes.Count != 1 || !string.Equals(modes[0], "Exclusive", StringComparison.Ordinal))
        {
            throw new SyncRunLockLostException(resource);
        }
    }

    /// <summary>
    /// Returns a tracked entity to its last persisted values, discarding changes a failed
    /// attempt made in memory.
    /// </summary>
    private void ResetToPersisted(object entity)
    {
        var entry = _context.Entry(entity);

        if (entry.State is EntityState.Modified)
        {
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
        }
    }

    private void ThrowIfAmbientTransaction()
    {
        if (_context.Database.CurrentTransaction is not null)
        {
            // Nesting would make "committed" mean "committed if the caller commits too", and the
            // pipeline relies on the stronger meaning to decide whether a run succeeded.
            throw new InvalidOperationException(
                "The sync pipeline owns its transaction boundary; do not call it inside an open transaction.");
        }
    }

    private static SyncRun NewRun(SyncRunRequest request, DateTimeOffset nowUtc)
        => SyncRun.Start(
            request.TenantId,
            request.JobType,
            request.CorrelationId,
            nowUtc,
            request.RequestedMode,
            request.PeriodKey);

    private static SqlParameter ResourceParameter(string resource)
        => new("@resource", System.Data.SqlDbType.NVarChar, 255) { Value = resource };

    private sealed class AdoptionState(SyncRunRequest request, SyncRun resumeFrom, DateTimeOffset nowUtc)
    {
        public SyncRunRequest Request { get; } = request;

        public SyncRun ResumeFrom { get; } = resumeFrom;

        public DateTimeOffset NowUtc { get; } = nowUtc;

        public SyncRun? Run { get; set; }

        public bool Verified { get; set; }
    }

    private sealed class CompletionState<T>(SyncRun run, Func<CancellationToken, Task<T>> operation)
    {
        public SyncRun Run { get; } = run;

        public Func<CancellationToken, Task<T>> Operation { get; } = operation;

        public bool CommitAttempted { get; set; }

        public bool Verified { get; set; }
    }

    private sealed class RunLock(SyncRunStore store, string resource) : ISyncRunLock
    {
        private int _released;

        public string Resource { get; } = resource;

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _released, 1) == 0 ? store.ReleaseLockAsync(Resource) : ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 4150, Level = LogLevel.Information, Message = "Run lock {Resource} is held elsewhere (sp_getapplock returned {Result}).")]
    private partial void LogLockNotAcquired(string resource, int result);

    [LoggerMessage(EventId = 4151, Level = LogLevel.Warning, Message = "Could not release run lock {Resource}; the pool has been cleared.")]
    private partial void LogReleaseFailed(Exception exception, string resource);

    [LoggerMessage(EventId = 4152, Level = LogLevel.Warning, Message = "Releasing run lock {Resource} while a transaction is open.")]
    private partial void LogReleaseInsideTransaction(string resource);

    [LoggerMessage(EventId = 4153, Level = LogLevel.Warning, Message = "Run {SyncRunId} could not adopt the staging of run {ResumeFromSyncRunId}: none was left. Starting from the beginning.")]
    private partial void LogAdoptionFoundNoStaging(Guid resumeFromSyncRunId, Guid syncRunId);

    [LoggerMessage(EventId = 4154, Level = LogLevel.Warning, Message = "The commit for run {SyncRunId} reported an error but the database shows it succeeded.")]
    private partial void LogCommitVerified(Exception exception, Guid syncRunId);
}

/// <summary>
/// The session holding the run lock was lost before the merge committed, so another worker may
/// now own the run. The merge is rolled back.
/// </summary>
public sealed class SyncRunLockLostException : Exception
{
    public SyncRunLockLostException()
    {
    }

    public SyncRunLockLostException(string resource)
        : base($"The run lock {resource} is no longer held by this session; the merge was not committed.")
    {
    }

    public SyncRunLockLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
