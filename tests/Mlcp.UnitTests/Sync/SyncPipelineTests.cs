using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Sync;
using Mlcp.Domain.Sync;
using Mlcp.Shared.Resilience;

namespace Mlcp.UnitTests.Sync;

/// <summary>
/// The pipeline is where CLAUDE.md rule 6 and ADR-024 are enforced: lock, staging, gate, then
/// one transaction holding the merge and the run status. What matters most is the failure
/// paths — every one of them must leave the live tables untouched and say why on the run.
/// </summary>
public class SyncPipelineTests
{
    private static readonly Guid Tenant = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Models the real store's contract: the merge and the run save commit together; a failure
    /// before commit resets the run to its persisted state and throws; the caller's token is not
    /// observed once the commit is on the wire.
    /// </summary>
    private sealed class FakeRunStore : ISyncRunStore
    {
        public bool LockAvailable { get; set; } = true;

        public bool LockReleased { get; private set; }

        public SyncRun? Run { get; private set; }

        public SyncRun? Skipped { get; private set; }

        public SyncRun? Resumable { get; set; }

        public int? Baseline { get; set; }

        public int TransactionCount { get; private set; }

        public int SaveCount { get; private set; }

        public string? LastSavedToken { get; private set; }

        /// <summary>True once a merge has committed: the fake live table.</summary>
        public bool LiveCommitted { get; private set; }

        public Exception? FailBeforeCommit { get; set; }

        public Exception? FailAfterCommit { get; set; }

        public CancellationTokenSource? CancelAfterCommit { get; set; }

        public Task<ISyncRunLock?> TryAcquireLockAsync(Guid tenantId, SyncJobType jobType, CancellationToken ct)
            => Task.FromResult<ISyncRunLock?>(LockAvailable ? new Lock(this) : null);

        public Task<SyncRun> RecordSkippedAsync(SyncRunRequest request, string reason, CancellationToken ct)
        {
            Skipped = SyncRun.RecordSkipped(request.TenantId, request.JobType, request.CorrelationId, request.RequestedMode, request.PeriodKey, reason, Now);
            return Task.FromResult(Skipped);
        }

        public Task<SyncRun?> FindResumableRunAsync(SyncRunRequest request, DateTimeOffset notBeforeUtc, CancellationToken ct)
            => Task.FromResult(Resumable);

        public Task<SyncRun> StartRunAsync(SyncRunRequest request, SyncRun? resumeFrom, CancellationToken ct)
        {
            Run = SyncRun.Start(request.TenantId, request.JobType, request.CorrelationId, Now, request.RequestedMode, request.PeriodKey);

            if (resumeFrom is not null)
            {
                Run.ResumeFrom(resumeFrom, Now);
                resumeFrom.Supersede(Run.SyncRunId, Now);
            }

            return Task.FromResult(Run);
        }

        public Task<SyncGateBaseline?> GetBaselineAsync(SyncRun run, CancellationToken ct)
            => Task.FromResult(Baseline is { } b ? new SyncGateBaseline(Guid.NewGuid(), b, Now.AddDays(-1)) : null);

        public Task SaveAsync(SyncRun run, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SaveCount++;
            LastSavedToken = run.ContinuationToken;
            return Task.CompletedTask;
        }

        public async Task<T> CompleteInTransactionAsync<T>(SyncRun run, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            TransactionCount++;
            var snapshot = Snapshot.Take(run);

            try
            {
                var result = await operation(ct);

                if (FailBeforeCommit is not null)
                {
                    throw FailBeforeCommit;
                }

                LiveCommitted = true;
                CancelAfterCommit?.Cancel();

                if (FailAfterCommit is not null)
                {
                    throw FailAfterCommit;
                }

                return result;
            }
            catch when (!LiveCommitted)
            {
                snapshot.Restore(run);
                throw;
            }
        }

        private sealed class Lock(FakeRunStore store) : ISyncRunLock
        {
            public string Resource => "fake";

            public ValueTask DisposeAsync()
            {
                store.LockReleased = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Restores a run's private state, standing in for EF's reset to original values.</summary>
    private sealed class Snapshot
    {
        private readonly List<(PropertyInfo Property, object? Value)> _values = [];

        public static Snapshot Take(object entity)
        {
            var snapshot = new Snapshot();

            for (var type = entity.GetType(); type is not null; type = type.BaseType)
            {
                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (property.GetSetMethod(nonPublic: true) is not null)
                    {
                        snapshot._values.Add((property, property.GetValue(entity)));
                    }
                }
            }

            return snapshot;
        }

        public void Restore(object entity)
        {
            foreach (var (property, value) in _values)
            {
                property.GetSetMethod(nonPublic: true)!.Invoke(entity, [value]);
            }
        }
    }

    private sealed class FakeGateContext : ISyncGateContext
    {
        public bool Invoiced { get; set; }

        public int Calls { get; private set; }

        public Task<bool> InvoiceIssuedSinceAsync(Guid tenantId, string periodKey, DateTimeOffset sinceUtc, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Invoiced);
        }
    }

    private sealed class FakeOverrides : ISyncGateOverrideStore
    {
        public SyncGateOverride? Available { get; set; }

        public bool ConsumeSucceeds { get; set; } = true;

        public List<Guid> ConsumedBy { get; } = [];

        public Task AddAsync(SyncGateOverride approval, CancellationToken ct) => Task.CompletedTask;

        public Task<SyncGateOverride?> FindAvailableAsync(Guid tenantId, SyncJobType jobType, DateTimeOffset nowUtc, CancellationToken ct)
            => Task.FromResult(Available is { } a && a.IsAvailableAt(nowUtc) ? a : null);

        public Task<bool> TryConsumeAsync(Guid id, Guid syncRunId, DateTimeOffset nowUtc, CancellationToken ct)
        {
            if (!ConsumeSucceeds || Available is null || !Available.IsAvailableAt(nowUtc))
            {
                return Task.FromResult(false);
            }

            Available.Consume(syncRunId, nowUtc);
            ConsumedBy.Add(syncRunId);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeHandler : ISyncJobHandler
    {
        public SyncJobType JobType { get; init; } = SyncJobType.LicenseSkuSync;

        public StagingSummary Staged { get; init; } = new(10);

        public Exception? FetchThrows { get; init; }

        public string? CheckpointBeforeThrow { get; init; }

        public Func<CancellationToken, Task>? DuringFetch { get; init; }

        public SyncRunContext? SeenContext { get; private set; }

        public bool Fetched { get; private set; }

        public bool Merged { get; private set; }

        public bool StagingCleared { get; private set; }

        public bool ClearTokenWasCancelled { get; private set; }

        public async Task<StagingSummary> FetchToStagingAsync(SyncRunContext context, CancellationToken ct)
        {
            Fetched = true;
            SeenContext = context;

            if (CheckpointBeforeThrow is not null)
            {
                await context.Progress.CheckpointAsync(CheckpointBeforeThrow, ct);
            }

            if (DuringFetch is not null)
            {
                await DuringFetch(ct);
            }

            return FetchThrows is not null ? throw FetchThrows : Staged;
        }

        public Task<int> MergeStagingToLiveAsync(SyncRunContext context, CancellationToken ct)
        {
            Merged = true;
            return Task.FromResult(Staged.StagedRowCount);
        }

        public Task ClearStagingAsync(SyncRunContext context, CancellationToken ct)
        {
            StagingCleared = true;
            ClearTokenWasCancelled = ct.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrottledException(TimeSpan retryAfter) : Exception("429"), IRetryLaterFailure
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    private static SyncPipeline Build(FakeRunStore store, FakeGateContext? gate = null, FakeOverrides? overrides = null)
        => new(
            store,
            gate ?? new FakeGateContext(),
            overrides ?? new FakeOverrides(),
            new FakeTimeProvider(Now),
            NullLogger<SyncPipeline>.Instance);

    private static Task<SyncRunOutcome> Run(FakeRunStore store, FakeHandler handler, CancellationToken ct = default)
        => Build(store).ExecuteAsync(Tenant, handler, "corr", ct);

    [Fact]
    public async Task A_healthy_run_merges_and_succeeds_in_one_transaction()
    {
        var store = new FakeRunStore { Baseline = 9 };
        var handler = new FakeHandler { Staged = new StagingSummary(10) };

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.Succeeded);
        outcome.LivePublished.Should().BeTrue();
        store.LiveCommitted.Should().BeTrue();
        store.TransactionCount.Should().Be(1, "the merge and the status are all-or-nothing");
        outcome.Run.RecordsProcessed.Should().Be(10);
        outcome.Run.StagedRowCount.Should().Be(10);
        handler.StagingCleared.Should().BeTrue();
        store.LockReleased.Should().BeTrue();
    }

    [Fact]
    public async Task A_gate_failure_never_reaches_the_live_table_and_clears_staging()
    {
        var store = new FakeRunStore { Baseline = 500 };
        var handler = new FakeHandler { Staged = StagingSummary.Empty };

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.ValidationFailed);
        outcome.LivePublished.Should().BeFalse();
        handler.Merged.Should().BeFalse();
        store.TransactionCount.Should().Be(0);
        outcome.Run.ValidationNotes.Should().Contain("partial response");
        handler.StagingCleared.Should().BeTrue();
        outcome.IsResumable.Should().BeFalse();
    }

    [Fact]
    public async Task A_lost_grant_is_surfaced_to_the_caller_not_applied()
    {
        var store = new FakeRunStore { Baseline = 10 };
        var handler = new FakeHandler { FetchThrows = new NeedsReconsentException(Tenant, MicrosoftProvider.Graph, 403) };

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.AbortedNeedsReconsent);
        outcome.RequiresReconsent.Should().BeTrue();
        outcome.ReconsentReason.Should().Contain("403");
        outcome.LivePublished.Should().BeFalse();
        handler.Merged.Should().BeFalse();
        handler.StagingCleared.Should().BeTrue();
    }

    [Fact]
    public async Task An_unexpected_failure_without_a_cursor_is_recorded_and_staging_cleared()
    {
        var store = new FakeRunStore { Baseline = 10 };
        var handler = new FakeHandler { FetchThrows = new HttpRequestException("Microsoft is down") };

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.Failed);
        outcome.RequiresReconsent.Should().BeFalse();
        outcome.Run.ErrorCode.Should().Be(nameof(HttpRequestException));
        outcome.Run.ErrorMessage.Should().Contain("Microsoft is down");
        outcome.IsResumable.Should().BeFalse();
        handler.StagingCleared.Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_after_a_checkpoint_keeps_staging_for_resume()
    {
        var store = new FakeRunStore();
        var handler = new FakeHandler { CheckpointBeforeThrow = "page-7", FetchThrows = new HttpRequestException("reset") };

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.Failed);
        outcome.IsResumable.Should().BeTrue();
        outcome.Run.ContinuationToken.Should().Be("page-7");
        store.LastSavedToken.Should().Be("page-7", "the checkpoint is persisted as it happens");
        handler.StagingCleared.Should().BeFalse();
    }

    [Fact]
    public async Task Throttling_reports_when_to_retry()
    {
        var store = new FakeRunStore();
        var handler = new FakeHandler { CheckpointBeforeThrow = "p2", FetchThrows = new ThrottledException(TimeSpan.FromMinutes(12)) };

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.Failed);
        outcome.RetryAfter.Should().Be(TimeSpan.FromMinutes(12));
        outcome.Run.ErrorCode.Should().Be("RetryLater");
        outcome.IsResumable.Should().BeTrue();
    }

    [Fact]
    public async Task A_save_failure_inside_the_transaction_rolls_back_the_merge_too()
    {
        // The reviewed bug: the merge used to commit first and the status save after it, so a
        // failing save produced a Failed run over published data. Now both roll back together.
        var store = new FakeRunStore { Baseline = 10, FailBeforeCommit = new InvalidOperationException("save failed") };
        var handler = new FakeHandler();

        var outcome = await Run(store, handler);

        store.LiveCommitted.Should().BeFalse();
        outcome.LivePublished.Should().BeFalse();
        outcome.Status.Should().Be(SyncRunStatus.Failed);
        outcome.Run.ErrorMessage.Should().Contain("save failed");
    }

    [Fact]
    public async Task An_error_after_the_commit_never_marks_the_run_failed()
    {
        var store = new FakeRunStore { Baseline = 10, FailAfterCommit = new InvalidOperationException("connection reset after commit") };
        var handler = new FakeHandler();

        var outcome = await Run(store, handler);

        store.LiveCommitted.Should().BeTrue();
        outcome.Status.Should().Be(SyncRunStatus.Succeeded);
        outcome.LivePublished.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_after_the_commit_still_yields_success()
    {
        using var cts = new CancellationTokenSource();
        var store = new FakeRunStore { Baseline = 10, CancelAfterCommit = cts };
        var handler = new FakeHandler();

        var outcome = await Run(store, handler, cts.Token);

        cts.IsCancellationRequested.Should().BeTrue();
        outcome.Status.Should().Be(SyncRunStatus.Succeeded);
        outcome.LivePublished.Should().BeTrue();
        handler.StagingCleared.Should().BeTrue();
        handler.ClearTokenWasCancelled.Should().BeFalse("cleanup after commit must not observe the caller's token");
        store.LockReleased.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_before_the_commit_records_a_resumable_failure_and_rethrows()
    {
        using var cts = new CancellationTokenSource();
        var store = new FakeRunStore();
        var handler = new FakeHandler
        {
            CheckpointBeforeThrow = "page-2",
            DuringFetch = ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };

        var act = () => Run(store, handler, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Run!.Status.Should().Be(SyncRunStatus.Failed);
        store.Run.ErrorCode.Should().Be("Cancelled");
        store.Run.IsResumable.Should().BeTrue();
        handler.StagingCleared.Should().BeFalse();
        store.LiveCommitted.Should().BeFalse();
        store.LockReleased.Should().BeTrue();
    }

    [Fact]
    public async Task A_new_run_adopts_a_resumable_run_and_continues_from_its_cursor()
    {
        var previous = SyncRun.Start(Tenant, SyncJobType.LicenseSkuSync, "old", Now.AddHours(-3));
        previous.RecordContinuation("page-40", Now.AddHours(-3));
        previous.Abandon("worker died", Now.AddHours(-1));

        var store = new FakeRunStore { Resumable = previous };
        var handler = new FakeHandler();

        var outcome = await Run(store, handler);

        handler.SeenContext!.ContinuationToken.Should().Be("page-40");
        handler.SeenContext.IsResumed.Should().BeTrue();
        outcome.Run.ResumedFromSyncRunId.Should().Be(previous.SyncRunId);
        previous.Status.Should().Be(SyncRunStatus.Superseded);
        previous.SupersededBySyncRunId.Should().Be(outcome.Run.SyncRunId);
        outcome.Status.Should().Be(SyncRunStatus.Succeeded);
    }

    [Fact]
    public async Task Lock_contention_records_a_skipped_run_and_runs_nothing()
    {
        var store = new FakeRunStore { LockAvailable = false };
        var handler = new FakeHandler();

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.Skipped);
        outcome.LivePublished.Should().BeFalse();
        outcome.Run.Should().BeSameAs(store.Skipped);
        store.Run.Should().BeNull("no running run is created without the lock");
        handler.Fetched.Should().BeFalse();
        handler.StagingCleared.Should().BeFalse("the staging belongs to the worker holding the lock");
    }

    [Fact]
    public async Task An_override_waives_a_drop_once_and_is_consumed_by_that_run()
    {
        var overrides = new FakeOverrides
        {
            Available = SyncGateOverride.Approve(Tenant, SyncJobType.LicenseSkuSync, "Customer cancelled most SKUs", "ops@mlcp.example", Now.AddHours(-1)),
        };

        var store = new FakeRunStore { Baseline = 1000 };
        var first = await Build(store, overrides: overrides).ExecuteAsync(Tenant, new FakeHandler { Staged = new StagingSummary(5) }, "c1", default);

        first.Status.Should().Be(SyncRunStatus.Succeeded);
        first.Run.ValidationNotes.Should().Contain("ops@mlcp.example").And.Contain(overrides.Available.SyncGateOverrideId.ToString());
        overrides.ConsumedBy.Should().Equal(first.Run.SyncRunId);

        var second = await Build(store, overrides: overrides).ExecuteAsync(Tenant, new FakeHandler { Staged = new StagingSummary(5) }, "c2", default);

        second.Status.Should().Be(SyncRunStatus.ValidationFailed, "a one-shot override cannot be reused");
    }

    [Fact]
    public async Task An_expired_override_does_not_open_the_gate()
    {
        var overrides = new FakeOverrides
        {
            Available = SyncGateOverride.Approve(Tenant, SyncJobType.LicenseSkuSync, "r", "ops", Now.AddHours(-2), TimeSpan.FromHours(1)),
        };

        var outcome = await Build(new FakeRunStore { Baseline = 1000 }, overrides: overrides)
            .ExecuteAsync(Tenant, new FakeHandler { Staged = new StagingSummary(5) }, "c", default);

        outcome.Status.Should().Be(SyncRunStatus.ValidationFailed);
        overrides.ConsumedBy.Should().BeEmpty();
    }

    [Fact]
    public async Task An_override_taken_by_another_run_before_the_merge_rolls_back_and_rejects()
    {
        var overrides = new FakeOverrides
        {
            Available = SyncGateOverride.Approve(Tenant, SyncJobType.LicenseSkuSync, "r", "ops", Now),
            ConsumeSucceeds = false,
        };

        var store = new FakeRunStore { Baseline = 1000 };
        var handler = new FakeHandler { Staged = new StagingSummary(5) };

        var outcome = await Build(store, overrides: overrides).ExecuteAsync(Tenant, handler, "c", default);

        outcome.Status.Should().Be(SyncRunStatus.ValidationFailed);
        store.LiveCommitted.Should().BeFalse();
        handler.Merged.Should().BeFalse("the consume check runs before the merge");
        handler.StagingCleared.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, SyncRunStatus.Succeeded)]
    [InlineData(false, SyncRunStatus.ValidationFailed)]
    public async Task A_same_period_drop_passes_only_after_an_invoice(bool invoiced, SyncRunStatus expected)
    {
        var gate = new FakeGateContext { Invoiced = invoiced };
        var request = new SyncRunRequest(Tenant, SyncJobType.TransactionSyncUnbilled, "c", PeriodKey: "2026-09");
        var handler = new FakeHandler { JobType = SyncJobType.TransactionSyncUnbilled, Staged = new StagingSummary(3) };

        var outcome = await Build(new FakeRunStore { Baseline = 900 }, gate).ExecuteAsync(request, handler, default);

        outcome.Status.Should().Be(expected);
        outcome.Run.PeriodKey.Should().Be("2026-09");
        gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_invoice_lookup_is_skipped_when_nothing_dropped()
    {
        var gate = new FakeGateContext();
        var request = new SyncRunRequest(Tenant, SyncJobType.TransactionSyncUnbilled, "c", PeriodKey: "2026-09");
        var handler = new FakeHandler { JobType = SyncJobType.TransactionSyncUnbilled, Staged = new StagingSummary(900) };

        await Build(new FakeRunStore { Baseline = 900 }, gate).ExecuteAsync(request, handler, default);

        gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_delta_job_staging_nothing_still_succeeds()
    {
        var store = new FakeRunStore { Baseline = 90_000 };
        var handler = new FakeHandler { JobType = SyncJobType.UserAssignmentSync, Staged = StagingSummary.Empty };

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.Succeeded);
        outcome.Run.LoadMode.Should().Be(SyncLoadMode.Incremental);
        handler.Merged.Should().BeTrue();
    }

    [Fact]
    public async Task A_requested_full_resync_of_a_delta_job_gets_the_full_gate()
    {
        // The 30-day full load of user assignments (ADR-024 §2) is compared with the last full
        // load, and a truncated one is blocked even though the job is incremental by default.
        var store = new FakeRunStore { Baseline = 90_000 };
        var handler = new FakeHandler { JobType = SyncJobType.UserAssignmentSync, Staged = new StagingSummary(100) };
        var request = new SyncRunRequest(Tenant, SyncJobType.UserAssignmentSync, "c", SyncLoadMode.Full);

        var outcome = await Build(store).ExecuteAsync(request, handler, default);

        outcome.Run.LoadMode.Should().Be(SyncLoadMode.Full);
        outcome.Status.Should().Be(SyncRunStatus.ValidationFailed);
        handler.SeenContext!.LoadMode.Should().Be(SyncLoadMode.Full);
    }

    [Fact]
    public async Task Field_violations_block_the_merge()
    {
        var store = new FakeRunStore { Baseline = 10 };
        var handler = new FakeHandler { Staged = new StagingSummary(10, ["CostAmount present with no Currency"]) };

        var outcome = await Run(store, handler);

        outcome.Status.Should().Be(SyncRunStatus.ValidationFailed);
        handler.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task A_correlation_id_is_required()
    {
        var act = () => Build(new FakeRunStore()).ExecuteAsync(Tenant, new FakeHandler(), "  ", default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_request_for_another_job_is_refused()
    {
        var act = () => Build(new FakeRunStore())
            .ExecuteAsync(new SyncRunRequest(Tenant, SyncJobType.InvoiceSync, "c"), new FakeHandler(), default);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
