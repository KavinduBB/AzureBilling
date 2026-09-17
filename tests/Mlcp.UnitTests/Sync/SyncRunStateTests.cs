using FluentAssertions;
using Mlcp.Domain.Common;
using Mlcp.Domain.Sync;

namespace Mlcp.UnitTests.Sync;

/// <summary>
/// The run status is a one-way state machine (ADR-024 §5): terminal states are final, except
/// that a resumable failure can be superseded once.
/// </summary>
public class SyncRunStateTests
{
    private static readonly Guid Tenant = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    private static SyncRun Running(SyncJobType job = SyncJobType.LicenseSkuSync, SyncLoadMode? mode = null, string? period = null)
        => SyncRun.Start(Tenant, job, "corr", Now, mode, period);

    [Fact]
    public void A_run_takes_its_job_default_mode_unless_one_is_requested()
    {
        Running(SyncJobType.UserAssignmentSync).LoadMode.Should().Be(SyncLoadMode.Incremental);
        Running(SyncJobType.UserAssignmentSync, SyncLoadMode.Full).LoadMode.Should().Be(SyncLoadMode.Full);
    }

    [Fact]
    public void A_blank_period_key_is_refused()
    {
        var act = () => Running(period: " ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Success_requires_a_staged_count_and_keeps_merged_separate()
    {
        var run = Running();
        var early = () => run.Succeed(1, "n", Now);
        early.Should().Throw<DomainException>();

        run.RecordStaged(500, Now);
        run.Succeed(3, "n", Now);

        run.StagedRowCount.Should().Be(500);
        run.RecordsProcessed.Should().Be(3);
    }

    public static TheoryData<Action<SyncRun>> TerminalTransitions => new()
    {
        r => r.Fail("x", "y", Now),
        r => r.FailValidation("n", Now),
        r => r.AbortNeedsReconsent("d", Now),
        r => r.Abandon("gone", Now),
        r => r.RecordContinuation("t", Now),
        r => r.RecordStaged(1, Now),
        r => r.Succeed(1, "n", Now),
    };

    [Theory]
    [MemberData(nameof(TerminalTransitions))]
    public void A_succeeded_run_cannot_change(Action<SyncRun> transition)
    {
        var run = Running();
        run.RecordStaged(1, Now);
        run.Succeed(1, "n", Now);

        var act = () => transition(run);
        act.Should().Throw<DomainException>();
        run.Status.Should().Be(SyncRunStatus.Succeeded);
    }

    [Fact]
    public void A_skipped_run_is_terminal_from_birth()
    {
        var run = SyncRun.RecordSkipped(Tenant, SyncJobType.LicenseSkuSync, "corr", null, null, "lock held", Now);

        run.Status.Should().Be(SyncRunStatus.Skipped);
        run.CompletedUtc.Should().Be(Now);
        var act = () => run.Fail("x", "y", Now);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_failed_run_with_a_cursor_is_resumable_and_can_be_superseded_once()
    {
        var old = Running();
        old.RecordContinuation("page-3", Now);
        old.Fail("HttpRequestException", "down", Now);

        old.IsResumable.Should().BeTrue();

        var next = Running();
        next.ResumeFrom(old, Now);
        old.Supersede(next.SyncRunId, Now);

        next.ContinuationToken.Should().Be("page-3");
        next.ResumedFromSyncRunId.Should().Be(old.SyncRunId);
        old.Status.Should().Be(SyncRunStatus.Superseded);
        old.SupersededBySyncRunId.Should().Be(next.SyncRunId);

        var again = () => old.Supersede(Guid.NewGuid(), Now);
        again.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_gate_rejection_is_not_resumable()
    {
        var run = Running();
        run.RecordContinuation("page-3", Now);
        run.FailValidation("drop", Now);

        run.IsResumable.Should().BeFalse();
        run.ContinuationToken.Should().BeNull();
    }

    [Fact]
    public void A_run_cannot_resume_a_different_period_or_mode()
    {
        var old = Running(period: "2026-08");
        old.RecordContinuation("t", Now);
        old.Fail("x", "y", Now);

        var otherPeriod = () => Running(period: "2026-09").ResumeFrom(old, Now);
        otherPeriod.Should().Throw<DomainException>();

        var otherMode = () => Running(mode: SyncLoadMode.Append, period: "2026-08").ResumeFrom(old, Now);
        otherMode.Should().Throw<DomainException>();
    }

    [Fact]
    public void Overrides_last_at_most_a_day_and_are_consumed_once()
    {
        var tooLong = () => SyncGateOverride.Approve(Tenant, SyncJobType.LicenseSkuSync, "r", "ops", Now, TimeSpan.FromHours(25));
        tooLong.Should().Throw<DomainException>();

        var approval = SyncGateOverride.Approve(Tenant, SyncJobType.LicenseSkuSync, "r", "ops", Now);
        approval.ExpiresUtc.Should().Be(Now.AddHours(24));
        approval.IsAvailableAt(Now.AddHours(24)).Should().BeFalse();

        approval.Consume(Guid.NewGuid(), Now);
        approval.IsAvailableAt(Now).Should().BeFalse();

        var twice = () => approval.Consume(Guid.NewGuid(), Now);
        twice.Should().Throw<DomainException>();
    }
}
