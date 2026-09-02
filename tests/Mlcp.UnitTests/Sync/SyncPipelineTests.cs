using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Sync;
using Mlcp.Domain.Sync;
using Mlcp.Shared.Resilience;

namespace Mlcp.UnitTests.Sync;

/// <summary>
/// The pipeline is where CLAUDE.md rule 6 is actually enforced: staging, then the gate, then a
/// single-transaction merge. What matters most is the failure paths — every one of them must
/// leave the live tables untouched and say why on the run record.
/// </summary>
public class SyncPipelineTests
{
    private static readonly Guid Tenant = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakeRunStore : ISyncRunStore
    {
        public SyncRun? Run { get; private set; }

        public int? Baseline { get; set; }

        public int TransactionCount { get; private set; }

        public Task<SyncRun> StartRunAsync(Guid tenantId, SyncJobType jobType, string correlationId, CancellationToken ct)
        {
            Run = SyncRun.Start(tenantId, jobType, correlationId, Now);
            return Task.FromResult(Run);
        }

        public Task<int?> GetLastSuccessfulRecordCountAsync(Guid tenantId, SyncJobType jobType, CancellationToken ct)
            => Task.FromResult(Baseline);

        public Task SaveAsync(SyncRun run, CancellationToken ct) => Task.CompletedTask;

        public Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            TransactionCount++;
            return action(ct);
        }
    }

    private sealed class FakeHandler : ISyncJobHandler
    {
        public SyncJobType JobType { get; init; } = SyncJobType.LicenseSkuSync;

        public StagingSummary Staged { get; init; } = new(10);

        public Exception? FetchThrows { get; init; }

        public bool Merged { get; private set; }

        public bool StagingCleared { get; private set; }

        public Task<StagingSummary> FetchToStagingAsync(SyncRunContext context, CancellationToken ct)
            => FetchThrows is not null ? Task.FromException<StagingSummary>(FetchThrows) : Task.FromResult(Staged);

        public Task<int> MergeStagingToLiveAsync(SyncRunContext context, CancellationToken ct)
        {
            Merged = true;
            return Task.FromResult(Staged.StagedRowCount);
        }

        public Task ClearStagingAsync(SyncRunContext context, CancellationToken ct)
        {
            StagingCleared = true;
            return Task.CompletedTask;
        }
    }

    private static SyncPipeline Build(FakeRunStore store)
        => new(store, new FakeTimeProvider(Now), NullLogger<SyncPipeline>.Instance);

    [Fact]
    public async Task A_healthy_run_merges_inside_a_transaction_and_succeeds()
    {
        var store = new FakeRunStore { Baseline = 9 };
        var handler = new FakeHandler { Staged = new StagingSummary(10) };

        var outcome = await Build(store).ExecuteAsync(Tenant, handler, "corr", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.Succeeded);
        outcome.LivePublished.Should().BeTrue();
        handler.Merged.Should().BeTrue();
        store.TransactionCount.Should().Be(1, "the merge is all-or-nothing");
        outcome.Run.RecordsProcessed.Should().Be(10);
    }

    [Fact]
    public async Task A_gate_failure_never_reaches_the_live_table()
    {
        // The whole point of the gate: an empty response must not wipe a populated table.
        var store = new FakeRunStore { Baseline = 500 };
        var handler = new FakeHandler { Staged = StagingSummary.Empty };

        var outcome = await Build(store).ExecuteAsync(Tenant, handler, "corr", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.ValidationFailed);
        outcome.LivePublished.Should().BeFalse();
        handler.Merged.Should().BeFalse();
        store.TransactionCount.Should().Be(0);
        outcome.Run.ValidationNotes.Should().Contain("partial response");
    }

    [Fact]
    public async Task A_lost_grant_aborts_without_retrying()
    {
        var store = new FakeRunStore { Baseline = 10 };

        var handler = new FakeHandler
        {
            FetchThrows = new NeedsReconsentException(Tenant, MicrosoftProvider.Graph, 403),
        };

        var outcome = await Build(store).ExecuteAsync(Tenant, handler, "corr", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.AbortedNeedsReconsent);
        outcome.LivePublished.Should().BeFalse();
        handler.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task An_unexpected_failure_is_recorded_rather_than_thrown()
    {
        // A worker processing many tenants must survive one of them failing.
        var store = new FakeRunStore { Baseline = 10 };
        var handler = new FakeHandler { FetchThrows = new HttpRequestException("Microsoft is down") };

        var outcome = await Build(store).ExecuteAsync(Tenant, handler, "corr", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.Failed);
        outcome.Run.ErrorCode.Should().Be(nameof(HttpRequestException));
        outcome.Run.ErrorMessage.Should().Contain("Microsoft is down");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staging_is_always_cleared(bool fails)
    {
        // Rows left tagged with a dead run would be merged by a later run that never fetched them.
        var store = new FakeRunStore { Baseline = 10 };

        var handler = new FakeHandler
        {
            FetchThrows = fails ? new InvalidOperationException("boom") : null,
        };

        await Build(store).ExecuteAsync(Tenant, handler, "corr", CancellationToken.None);

        handler.StagingCleared.Should().BeTrue();
    }

    [Fact]
    public async Task A_delta_job_staging_nothing_still_succeeds()
    {
        // The case ADR-013 exists for: an incremental run with no changes is healthy, not a
        // truncated response, and must not be blocked.
        var store = new FakeRunStore { Baseline = 90_000 };

        var handler = new FakeHandler
        {
            JobType = SyncJobType.UserAssignmentSync,
            Staged = StagingSummary.Empty,
        };

        var outcome = await Build(store).ExecuteAsync(Tenant, handler, "corr", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.Succeeded);
        handler.Merged.Should().BeTrue();
    }

    [Fact]
    public async Task Field_violations_block_the_merge()
    {
        var store = new FakeRunStore { Baseline = 10 };

        var handler = new FakeHandler
        {
            Staged = new StagingSummary(10, ["CostAmount present with no Currency"]),
        };

        var outcome = await Build(store).ExecuteAsync(Tenant, handler, "corr", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.ValidationFailed);
        handler.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task A_correlation_id_is_required()
    {
        var act = async () => await Build(new FakeRunStore())
            .ExecuteAsync(Tenant, new FakeHandler(), "  ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
