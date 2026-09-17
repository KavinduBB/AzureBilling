using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mlcp.Application.Sync;
using Mlcp.Domain.Sync;
using Mlcp.IntegrationTests.Infrastructure;
using Mlcp.Persistence;
using Mlcp.Persistence.Stores;
using Mlcp.Persistence.Sync;
using Mlcp.Shared.Tenancy;

namespace Mlcp.IntegrationTests.Sync;

/// <summary>
/// ADR-024 against a real SQL Server: the merge and the run status share one transaction under
/// the production retrying execution strategy, the run lock is a real session applock, resume
/// re-tags real staging rows, and the sweepers act on real rows.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SyncPipelineSqlTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;

    public SyncPipelineSqlTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (DockerAvailability.IsAvailable)
        {
            await _fixture.CreateTestTablesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (DockerAvailability.IsAvailable)
        {
            await _fixture.DropTestTablesAsync();
        }
    }

    [RequiresDockerFact]
    public async Task A_run_commits_under_the_production_retrying_strategy()
    {
        // The reviewed bug: with EnableRetryOnFailure, BeginTransactionAsync outside the
        // execution strategy throws "The configured execution strategy
        // 'SqlServerRetryingExecutionStrategy' does not support user-initiated transactions",
        // so every production sync run failed at the merge.
        var tenantId = await _fixture.SeedTenantAsync();
        await using var context = _fixture.CreateProductionContext(FixedTenantContext.For(tenantId));
        var handler = new SqlTestHandler(context) { RowsToStage = 4 };

        var outcome = await SyncSqlTestSupport.CreatePipeline(context).ExecuteAsync(tenantId, handler, "prod-options", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.Succeeded, outcome.Run.ErrorMessage);
        outcome.LivePublished.Should().BeTrue();

        var persisted = await _fixture.LoadRunAsync(outcome.Run.SyncRunId);
        persisted.Status.Should().Be(SyncRunStatus.Succeeded);
        persisted.StagedRowCount.Should().Be(4);
        persisted.RecordsProcessed.Should().Be(4);
        persisted.ContinuationToken.Should().BeNull();

        (await _fixture.CountLiveAsync(tenantId)).Should().Be(4);
        (await _fixture.CountStagingAsync(outcome.Run.SyncRunId)).Should().Be(0, "staging is cleared after success");
    }

    [RequiresDockerFact]
    public async Task A_failure_inside_the_transaction_rolls_back_the_merge_and_the_status_together()
    {
        var tenantId = await _fixture.SeedTenantAsync();
        await using var context = _fixture.CreateProductionContext(FixedTenantContext.For(tenantId));
        var handler = new SqlTestHandler(context) { ThrowAfterMerge = true };

        var outcome = await SyncSqlTestSupport.CreatePipeline(context).ExecuteAsync(tenantId, handler, "rollback", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.Failed);
        outcome.LivePublished.Should().BeFalse();
        (await _fixture.CountLiveAsync(tenantId)).Should().Be(0, "the merge rolled back with the status");

        var persisted = await _fixture.LoadRunAsync(outcome.Run.SyncRunId);
        persisted.Status.Should().Be(SyncRunStatus.Failed);
        persisted.IsResumable.Should().BeTrue("the handler checkpointed before failing");
        (await _fixture.CountStagingAsync(outcome.Run.SyncRunId)).Should().Be(3, "a resumable failure keeps its staging");
    }

    [RequiresDockerFact]
    public async Task The_baseline_is_the_last_staged_count_for_the_same_mode_and_period()
    {
        var tenantId = await _fixture.SeedTenantAsync();
        var now = DateTimeOffset.UtcNow;

        var full = SyncRun.Start(tenantId, SyncJobType.UserAssignmentSync, "a", now.AddHours(-3), SyncLoadMode.Full, null);
        full.RecordStaged(90_000, now);
        full.Succeed(12, "ok", now.AddHours(-3));
        await _fixture.AddRunAsync(full);

        var delta = SyncRun.Start(tenantId, SyncJobType.UserAssignmentSync, "b", now.AddHours(-1));
        delta.RecordStaged(5, now);
        delta.Succeed(5, "ok", now.AddHours(-1));
        await _fixture.AddRunAsync(delta);

        await using var context = _fixture.CreateContext(FixedTenantContext.For(tenantId));
        var store = SyncSqlTestSupport.CreateRunStore(context);

        var nextFull = SyncRun.Start(tenantId, SyncJobType.UserAssignmentSync, "c", now, SyncLoadMode.Full, null);
        var baseline = await store.GetBaselineAsync(nextFull, CancellationToken.None);

        baseline!.SyncRunId.Should().Be(full.SyncRunId);
        baseline.StagedRowCount.Should().Be(90_000, "the staged count, not the 12 rows the merge touched");

        var otherPeriod = SyncRun.Start(tenantId, SyncJobType.UserAssignmentSync, "d", now, SyncLoadMode.Full, "2026-10");
        (await store.GetBaselineAsync(otherPeriod, CancellationToken.None)).Should().BeNull();
    }

    [RequiresDockerFact]
    public async Task A_second_worker_cannot_take_the_run_lock_and_records_a_skip()
    {
        var tenantId = await _fixture.SeedTenantAsync();
        var tenant = FixedTenantContext.For(tenantId);

        await using var first = _fixture.CreateProductionContext(tenant);
        await using var second = _fixture.CreateProductionContext(tenant);

        var held = await SyncSqlTestSupport.CreateRunStore(first).TryAcquireLockAsync(tenantId, SyncJobType.LicenseSkuSync, CancellationToken.None);
        held.Should().NotBeNull();

        var handler = new SqlTestHandler(second);
        var outcome = await SyncSqlTestSupport.CreatePipeline(second).ExecuteAsync(tenantId, handler, "contended", CancellationToken.None);

        outcome.Status.Should().Be(SyncRunStatus.Skipped);
        handler.SeenContext.Should().BeNull("the loser does no work");
        (await _fixture.LoadRunAsync(outcome.Run.SyncRunId)).Status.Should().Be(SyncRunStatus.Skipped);

        // A different job for the same tenant is not blocked.
        var otherJob = await SyncSqlTestSupport.CreateRunStore(second).TryAcquireLockAsync(tenantId, SyncJobType.InvoiceSync, CancellationToken.None);
        otherJob.Should().NotBeNull();
        await otherJob!.DisposeAsync();

        await held!.DisposeAsync();

        await using var third = _fixture.CreateProductionContext(tenant);
        var afterRelease = await SyncSqlTestSupport.CreatePipeline(third)
            .ExecuteAsync(tenantId, new SqlTestHandler(third), "after-release", CancellationToken.None);

        afterRelease.Status.Should().Be(SyncRunStatus.Succeeded, afterRelease.Run.ErrorMessage);
    }

    [RequiresDockerFact]
    public async Task A_merge_does_not_commit_once_the_lock_has_been_lost()
    {
        var tenantId = await _fixture.SeedTenantAsync();
        await using var context = _fixture.CreateContext(FixedTenantContext.For(tenantId));
        var store = SyncSqlTestSupport.CreateRunStore(context);

        await using var held = await store.TryAcquireLockAsync(tenantId, SyncJobType.LicenseSkuSync, CancellationToken.None);
        var run = await store.StartRunAsync(new SyncRunRequest(tenantId, SyncJobType.LicenseSkuSync, "lost"), null, CancellationToken.None);
        run.RecordStaged(1, DateTimeOffset.UtcNow);
        await store.SaveAsync(run, CancellationToken.None);

        // Simulates the session losing its lock (in production: a dropped connection).
        await context.Database.ExecuteSqlRawAsync(
            "EXEC sys.sp_releaseapplock @Resource = @r, @LockOwner = N'Session', @DbPrincipal = N'public'",
            new Microsoft.Data.SqlClient.SqlParameter("@r", SyncRunStore.LockResource(tenantId, SyncJobType.LicenseSkuSync)));

        var act = () => store.CompleteInTransactionAsync(
            run,
            _ =>
            {
                run.Succeed(0, "n", DateTimeOffset.UtcNow);
                return Task.FromResult(0);
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<SyncRunLockLostException>();
        run.Status.Should().Be(SyncRunStatus.Running, "the store resets the run when nothing committed");
        (await _fixture.LoadRunAsync(run.SyncRunId)).Status.Should().Be(SyncRunStatus.Running);
    }

    [RequiresDockerFact]
    public async Task A_new_run_adopts_the_staging_and_cursor_of_a_failed_run()
    {
        var tenantId = await _fixture.SeedTenantAsync();
        var now = DateTimeOffset.UtcNow;

        var failed = SyncRun.Start(tenantId, SyncJobType.LicenseSkuSync, "old", now.AddHours(-2));
        failed.RecordContinuation("page-9", now.AddHours(-2));
        failed.Fail("HttpRequestException", "reset", now.AddHours(-1));
        await _fixture.AddRunAsync(failed);
        await _fixture.InsertStagingAsync(failed.SyncRunId, tenantId, 7);

        await using var context = _fixture.CreateProductionContext(FixedTenantContext.For(tenantId));
        var handler = new SqlTestHandler(context) { RowsToStage = 2 };

        var outcome = await SyncSqlTestSupport.CreatePipeline(context).ExecuteAsync(tenantId, handler, "resume", CancellationToken.None);

        handler.SeenContext!.ContinuationToken.Should().Be("page-9");
        handler.AdoptedRows.Should().Be(7, "the old run's staging was re-tagged to the new run");
        outcome.Status.Should().Be(SyncRunStatus.Succeeded, outcome.Run.ErrorMessage);
        outcome.Run.StagedRowCount.Should().Be(9);
        outcome.Run.ResumedFromSyncRunId.Should().Be(failed.SyncRunId);
        (await _fixture.CountLiveAsync(tenantId)).Should().Be(9);

        var old = await _fixture.LoadRunAsync(failed.SyncRunId);
        old.Status.Should().Be(SyncRunStatus.Superseded);
        old.SupersededBySyncRunId.Should().Be(outcome.Run.SyncRunId);
        (await _fixture.CountStagingAsync(failed.SyncRunId)).Should().Be(0);
    }

    [RequiresDockerFact]
    public async Task A_failed_run_without_staging_is_not_adopted()
    {
        var tenantId = await _fixture.SeedTenantAsync();
        var now = DateTimeOffset.UtcNow;

        var failed = SyncRun.Start(tenantId, SyncJobType.LicenseSkuSync, "old", now.AddHours(-2));
        failed.RecordContinuation("page-9", now.AddHours(-2));
        failed.Fail("HttpRequestException", "reset", now.AddHours(-1));
        await _fixture.AddRunAsync(failed);

        await using var context = _fixture.CreateProductionContext(FixedTenantContext.For(tenantId));
        var handler = new SqlTestHandler(context);

        var outcome = await SyncSqlTestSupport.CreatePipeline(context).ExecuteAsync(tenantId, handler, "fresh", CancellationToken.None);

        handler.SeenContext!.ContinuationToken.Should().BeNull();
        outcome.Run.ResumedFromSyncRunId.Should().BeNull();
        (await _fixture.LoadRunAsync(failed.SyncRunId)).Status.Should().Be(SyncRunStatus.Failed);
    }

    [RequiresDockerFact]
    public async Task An_override_is_consumed_once_and_released_by_a_rollback()
    {
        var tenantId = await _fixture.SeedTenantAsync();
        await using var context = _fixture.CreateProductionContext(FixedTenantContext.For(tenantId));
        var overrides = new SyncGateOverrideStore(context);
        var now = DateTimeOffset.UtcNow;

        var approval = SyncGateOverride.Approve(tenantId, SyncJobType.LicenseSkuSync, "genuine drop", "ops@mlcp.example", now);
        await overrides.AddAsync(approval, CancellationToken.None);

        var store = SyncSqlTestSupport.CreateRunStore(context);
        var run = await store.StartRunAsync(new SyncRunRequest(tenantId, SyncJobType.LicenseSkuSync, "ovr"), null, CancellationToken.None);
        run.RecordStaged(1, now);
        await store.SaveAsync(run, CancellationToken.None);

        var rolledBack = () => store.CompleteInTransactionAsync<int>(
            run,
            async ct =>
            {
                (await overrides.TryConsumeAsync(approval.SyncGateOverrideId, run.SyncRunId, now, ct)).Should().BeTrue();
                throw new InvalidOperationException("merge failed");
            },
            CancellationToken.None);

        await rolledBack.Should().ThrowAsync<InvalidOperationException>();
        (await overrides.FindAvailableAsync(tenantId, SyncJobType.LicenseSkuSync, now, CancellationToken.None))
            .Should().NotBeNull("a merge that rolled back must not burn the approval");

        (await overrides.TryConsumeAsync(approval.SyncGateOverrideId, run.SyncRunId, now, CancellationToken.None)).Should().BeTrue();
        (await overrides.TryConsumeAsync(approval.SyncGateOverrideId, Guid.NewGuid(), now, CancellationToken.None)).Should().BeFalse();
        (await overrides.FindAvailableAsync(tenantId, SyncJobType.LicenseSkuSync, now, CancellationToken.None)).Should().BeNull();

        var expired = SyncGateOverride.Approve(tenantId, SyncJobType.InvoiceSync, "r", "ops", now.AddHours(-2), TimeSpan.FromHours(1));
        await overrides.AddAsync(expired, CancellationToken.None);
        (await overrides.TryConsumeAsync(expired.SyncGateOverrideId, run.SyncRunId, now, CancellationToken.None)).Should().BeFalse();
    }

    [RequiresDockerFact]
    public async Task The_stuck_run_sweeper_abandons_only_runs_past_twice_their_max_duration()
    {
        var tenantId = await _fixture.SeedTenantAsync();
        var now = DateTimeOffset.UtcNow;

        var stuck = SyncRun.Start(tenantId, SyncJobType.LicenseSkuSync, "stuck", now.AddHours(-5));
        var slowButAlive = SyncRun.Start(tenantId, SyncJobType.UserAssignmentSync, "slow", now.AddHours(-5));
        var recent = SyncRun.Start(tenantId, SyncJobType.InvoiceSync, "recent", now.AddHours(-1));

        foreach (var run in new[] { stuck, slowButAlive, recent })
        {
            await _fixture.AddRunAsync(run);
        }

        var maintenance = new SyncMaintenanceStore(new SystemContexts(_fixture), TimeProvider.System, NullLogger<SyncMaintenanceStore>.Instance);

        var abandoned = await maintenance.AbandonStuckRunsAsync(CancellationToken.None);

        abandoned.Should().BeGreaterThanOrEqualTo(1);
        (await _fixture.LoadRunAsync(stuck.SyncRunId)).Status.Should().Be(SyncRunStatus.Abandoned);
        (await _fixture.LoadRunAsync(slowButAlive.SyncRunId)).Status.Should().Be(SyncRunStatus.Running, "its job may run for 4 h, so 8 h before it is stuck");
        (await _fixture.LoadRunAsync(recent.SyncRunId)).Status.Should().Be(SyncRunStatus.Running);
    }

    [RequiresDockerFact]
    public async Task The_staging_sweeper_handles_no_tables_and_keeps_only_resumable_or_running_staging()
    {
        var maintenance = new SyncMaintenanceStore(new SystemContexts(_fixture), TimeProvider.System, NullLogger<SyncMaintenanceStore>.Instance);

        await _fixture.DropTestTablesAsync();
        (await maintenance.SweepStagingAsync(CancellationToken.None)).Should().Be(0, "a database without staging tables is normal");
        await _fixture.CreateTestTablesAsync();

        var tenantId = await _fixture.SeedTenantAsync();
        var now = DateTimeOffset.UtcNow;

        var running = SyncRun.Start(tenantId, SyncJobType.LicenseSkuSync, "running", now.AddDays(-3));
        var recentFailure = SyncRun.Start(tenantId, SyncJobType.InvoiceSync, "recent", now.AddHours(-3));
        recentFailure.RecordContinuation("t", now.AddHours(-3));
        recentFailure.Fail("x", "y", now.AddHours(-2));
        var oldFailure = SyncRun.Start(tenantId, SyncJobType.ReservationSync, "old", now.AddHours(-40));
        oldFailure.RecordContinuation("t", now.AddHours(-40));
        oldFailure.Fail("x", "y", now.AddHours(-30));

        foreach (var run in new[] { running, recentFailure, oldFailure })
        {
            await _fixture.AddRunAsync(run);
        }

        var orphan = Guid.NewGuid();
        await _fixture.InsertStagingAsync(running.SyncRunId, tenantId, 2);
        await _fixture.InsertStagingAsync(recentFailure.SyncRunId, tenantId, 3);
        await _fixture.InsertStagingAsync(oldFailure.SyncRunId, tenantId, 4);
        await _fixture.InsertStagingAsync(orphan, tenantId, 5);

        var deleted = await maintenance.SweepStagingAsync(CancellationToken.None);

        deleted.Should().Be(9);
        (await _fixture.CountStagingAsync(running.SyncRunId)).Should().Be(2);
        (await _fixture.CountStagingAsync(recentFailure.SyncRunId)).Should().Be(3);
        (await _fixture.CountStagingAsync(oldFailure.SyncRunId)).Should().Be(0);
        (await _fixture.CountStagingAsync(orphan)).Should().Be(0);
    }

    [RequiresDockerFact]
    public async Task The_staging_store_ignores_tables_without_a_run_column()
    {
        await _fixture.ExecuteAsync("IF OBJECT_ID(N'dbo.staging_NoRunColumn') IS NULL CREATE TABLE dbo.staging_NoRunColumn (Id int NOT NULL);");

        try
        {
            await using var context = _fixture.CreateContext(FixedTenantContext.System);
            var tables = await new SqlStagingStore(context).DiscoverTablesAsync(CancellationToken.None);

            tables.Should().Contain("[dbo].[staging_Test]").And.NotContain("[dbo].[staging_NoRunColumn]");
        }
        finally
        {
            await _fixture.ExecuteAsync("DROP TABLE IF EXISTS dbo.staging_NoRunColumn;");
        }
    }

    private sealed class SystemContexts(SqlServerFixture fixture) : ISystemDbContextFactory
    {
        public MlcpDbContext CreateDbContext() => fixture.CreateProductionContext(FixedTenantContext.System);
    }
}
