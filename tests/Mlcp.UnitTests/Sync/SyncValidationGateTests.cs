using FluentAssertions;
using Mlcp.Domain.Sync;

namespace Mlcp.UnitTests.Sync;

/// <summary>
/// The gate is the only thing standing between a bad Microsoft response and a customer's live
/// data, so its behaviour is pinned in both directions: it must reject the responses that
/// would destroy data, and it must not reject the ordinary ones (ADR-013, ADR-024).
/// </summary>
public class SyncValidationGateTests
{
    private static readonly DateTimeOffset BaselineStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static SyncGateBaseline Baseline(int staged) => new(Guid.NewGuid(), staged, BaselineStart);

    private static SyncValidationResult Full(int staged, int? baseline, string? period = null, bool invoiced = false, SyncGateOverrideGrant? grant = null)
        => SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncLoadMode.Full, staged, baseline is null ? null : Baseline(baseline.Value))
            {
                PeriodKey = period,
                InvoiceIssuedSinceBaseline = invoiced,
                Override = grant,
            });

    private static readonly SyncGateOverrideGrant Grant = new(Guid.NewGuid(), "ops@mlcp.example", "Tenant cancelled 80% of subscriptions");

    [Fact]
    public void The_very_first_run_has_no_volume_check()
    {
        var result = Full(staged: 3, baseline: null);

        result.Passed.Should().BeTrue();
        result.Decision.Should().Be(SyncGateDecision.NoBaseline);
        result.ConsumesOverride.Should().BeFalse();
    }

    [Fact]
    public void The_first_run_of_a_new_period_has_no_volume_check()
    {
        // The pipeline finds no baseline for the new period key, so even a near-empty unbilled
        // set on day one of a period passes (ADR-024 §3).
        var result = Full(staged: 1, baseline: null, period: "2026-10");

        result.Passed.Should().BeTrue();
        result.Notes.Should().Contain("2026-10");
    }

    [Fact]
    public void A_full_load_returning_nothing_against_a_populated_baseline_is_rejected()
    {
        var result = Full(staged: 0, baseline: 42);

        result.Passed.Should().BeFalse();
        result.Decision.Should().Be(SyncGateDecision.BlockedEmpty);
        result.IsVolumeBlock.Should().BeTrue();
        result.Notes.Should().Contain("partial response");
    }

    [Fact]
    public void A_full_load_losing_more_than_half_is_rejected()
    {
        var result = Full(staged: 49, baseline: 100);

        result.Passed.Should().BeFalse();
        result.Decision.Should().Be(SyncGateDecision.BlockedDrop);
    }

    [Fact]
    public void Losing_exactly_half_is_accepted()
    {
        // The rule is "drops greater than 50%", so the boundary itself passes.
        Full(staged: 50, baseline: 100).Passed.Should().BeTrue();
        Full(staged: 2, baseline: 5).Passed.Should().BeFalse("2 < 2.5");
        Full(staged: 3, baseline: 5).Passed.Should().BeTrue();
    }

    [Fact]
    public void A_zero_baseline_cannot_be_dropped_from()
    {
        var result = Full(staged: 0, baseline: 0);

        result.Passed.Should().BeTrue();
        result.Decision.Should().Be(SyncGateDecision.NoBaseline);
    }

    [Fact]
    public void Growth_is_accepted()
        => Full(staged: 500, baseline: 100).Decision.Should().Be(SyncGateDecision.WithinTolerance);

    [Fact]
    public void A_same_period_drop_without_an_invoice_is_rejected()
    {
        var result = Full(staged: 2, baseline: 1000, period: "2026-09", invoiced: false);

        result.Passed.Should().BeFalse();
        result.Notes.Should().Contain("2026-09");
    }

    [Fact]
    public void A_same_period_drop_after_an_invoice_is_accepted()
    {
        var result = Full(staged: 2, baseline: 1000, period: "2026-09", invoiced: true);

        result.Passed.Should().BeTrue();
        result.Decision.Should().Be(SyncGateDecision.AllowedInvoiceIssued);
    }

    [Fact]
    public void An_invoice_does_not_excuse_a_drop_in_a_job_that_is_not_period_scoped()
    {
        // Without a period key the invoice flag means nothing; a stray true must not open the gate.
        Full(staged: 2, baseline: 1000, period: null, invoiced: true).Passed.Should().BeFalse();
    }

    [Fact]
    public void An_override_lets_a_drop_through_and_is_consumed_and_audited()
    {
        var result = Full(staged: 0, baseline: 1000, grant: Grant);

        result.Passed.Should().BeTrue();
        result.Decision.Should().Be(SyncGateDecision.AllowedByOverride);
        result.ConsumesOverride.Should().BeTrue();
        result.Notes.Should().Contain(Grant.SyncGateOverrideId.ToString()).And.Contain(Grant.ApprovedBy);
    }

    [Fact]
    public void An_override_is_consumed_by_the_next_full_load_even_when_not_needed()
    {
        // Otherwise an approval would linger and silently waive a real truncation later on.
        var result = Full(staged: 1000, baseline: 1000, grant: Grant);

        result.Decision.Should().Be(SyncGateDecision.WithinTolerance);
        result.ConsumesOverride.Should().BeTrue();
        result.Notes.Should().Contain("consumed");
    }

    [Fact]
    public void An_override_cannot_waive_field_violations()
    {
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncLoadMode.Full, 10, Baseline(10), ["CostAmount present with no Currency"])
            {
                Override = Grant,
            });

        result.Passed.Should().BeFalse();
        result.Decision.Should().Be(SyncGateDecision.FieldViolations);
        result.ConsumesOverride.Should().BeFalse();
    }

    [Theory]
    [InlineData(SyncLoadMode.Incremental)]
    [InlineData(SyncLoadMode.Append)]
    public void Incremental_and_append_loads_staging_nothing_are_accepted(SyncLoadMode mode)
    {
        var result = SyncValidationGate.Evaluate(new SyncValidationInput(mode, 0, Baseline(90_000)) { Override = Grant });

        result.Passed.Should().BeTrue();
        result.Decision.Should().Be(SyncGateDecision.VolumeNotApplicable);
        result.Notes.Should().Contain("volume checks not applicable");

        // An override is for a full load; a daily delta must not burn it.
        result.ConsumesOverride.Should().BeFalse();
    }

    [Fact]
    public void Field_violations_reject_the_run_whatever_the_mode()
    {
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncLoadMode.Append, 10_000, Baseline(10_000), ["CostAmount present with no Currency on 3 rows"]));

        result.Passed.Should().BeFalse();
        result.Notes.Should().Contain("Currency");
    }

    [Fact]
    public void Notes_are_recorded_on_a_pass()
        => Full(staged: 120, baseline: 100).Notes.Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void Unmapped_job_types_default_to_the_strictest_gate()
        => SyncJobType.Unknown.LoadMode().Should().Be(SyncLoadMode.Full);

    [Theory]
    [InlineData(SyncJobType.LicenseSkuSync, SyncLoadMode.Full)]
    [InlineData(SyncJobType.UserAssignmentSync, SyncLoadMode.Incremental)]
    [InlineData(SyncJobType.AzureCostDetailSync, SyncLoadMode.Append)]
    [InlineData(SyncJobType.TransactionSyncBilled, SyncLoadMode.Append)]
    [InlineData(SyncJobType.TransactionSyncUnbilled, SyncLoadMode.Full)]
    public void Job_default_load_modes_match_the_documented_sync_behaviour(SyncJobType jobType, SyncLoadMode expected)
        => jobType.LoadMode().Should().Be(expected);

    [Fact]
    public void Jobs_have_a_max_duration_defaulting_to_two_hours()
    {
        SyncJobType.LicenseSkuSync.MaxDuration().Should().Be(TimeSpan.FromHours(2));
        SyncJobType.LicenseSkuSync.StuckAfter().Should().Be(TimeSpan.FromHours(4));
        SyncJobType.UserAssignmentSync.MaxDuration().Should().BeGreaterThan(TimeSpan.FromHours(2));
    }
}
