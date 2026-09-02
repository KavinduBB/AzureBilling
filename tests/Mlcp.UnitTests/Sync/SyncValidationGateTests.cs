using FluentAssertions;
using Mlcp.Domain.Sync;

namespace Mlcp.UnitTests.Sync;

/// <summary>
/// The gate is the only thing standing between a bad Microsoft response and a customer's live
/// data, so its behaviour is pinned in both directions: it must reject the responses that
/// would destroy data, and it must not reject the ordinary ones.
/// </summary>
public class SyncValidationGateTests
{
    private static readonly Guid AnyTenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void FullLoad_returning_nothing_against_a_populated_baseline_is_rejected()
    {
        // The failure this exists to prevent: an empty 200 wiping every licence a tenant has.
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncJobType.LicenseSkuSync, stagedRowCount: 0, lastSuccessfulRowCount: 42));

        result.Passed.Should().BeFalse();
        result.Notes.Should().Contain("partial response");
    }

    [Fact]
    public void FullLoad_losing_more_than_half_the_rows_is_rejected()
    {
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncJobType.LicenseSkuSync, stagedRowCount: 49, lastSuccessfulRowCount: 100));

        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void FullLoad_losing_exactly_half_the_rows_is_accepted()
    {
        // The rule is "drops greater than 50%", so the boundary itself passes. Pinned because
        // an off-by-one here either blocks legitimate runs or lets a halving through.
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncJobType.LicenseSkuSync, stagedRowCount: 50, lastSuccessfulRowCount: 100));

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void FullLoad_with_no_previous_run_is_accepted()
    {
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncJobType.LicenseSkuSync, stagedRowCount: 3, lastSuccessfulRowCount: null));

        result.Passed.Should().BeTrue();
        result.Notes.Should().Contain("no non-empty baseline");
    }

    [Fact]
    public void FullLoad_growing_is_accepted()
    {
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncJobType.LicenseSkuSync, stagedRowCount: 500, lastSuccessfulRowCount: 100));

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void Incremental_load_staging_nothing_is_accepted()
    {
        // A delta run that finds no changes stages zero rows. Applying the full-load volume
        // rule here would fail almost every user-assignment sync, which is how a gate stops
        // being believed. This is the case docs/05 P1-3 depends on.
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncJobType.UserAssignmentSync, stagedRowCount: 0, lastSuccessfulRowCount: 90_000));

        result.Passed.Should().BeTrue();
        result.Notes.Should().Contain("volume checks not applicable");
    }

    [Fact]
    public void Append_load_staging_nothing_is_accepted()
    {
        // A cost sync for a period with no new usage legitimately appends nothing.
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncJobType.AzureCostSummarySync, stagedRowCount: 0, lastSuccessfulRowCount: 10_000));

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void Field_violations_reject_the_run_whatever_the_volume()
    {
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(
                SyncJobType.AzureCostSummarySync,
                10_000,
                10_000,
                ["CostAmount present with no Currency on 3 rows"]));

        result.Passed.Should().BeFalse();
        result.Notes.Should().Contain("Currency");
    }

    [Fact]
    public void Unmapped_job_types_default_to_the_strictest_gate()
    {
        // A job added without declaring its load mode must not silently get the weakest checks.
        SyncJobType.Unknown.LoadMode().Should().Be(SyncLoadMode.Full);
    }

    [Theory]
    [InlineData(SyncJobType.LicenseSkuSync, SyncLoadMode.Full)]
    [InlineData(SyncJobType.UserAssignmentSync, SyncLoadMode.Incremental)]
    [InlineData(SyncJobType.AzureCostDetailSync, SyncLoadMode.Append)]
    [InlineData(SyncJobType.TransactionSyncBilled, SyncLoadMode.Append)]
    [InlineData(SyncJobType.TransactionSyncUnbilled, SyncLoadMode.Full)]
    public void Job_load_modes_match_the_documented_sync_behaviour(SyncJobType jobType, SyncLoadMode expected)
    {
        // Billed transactions are immutable once invoiced, so they append. Unbilled ones are
        // replaced wholesale each day, so they are a full load (docs/03 §6.1).
        jobType.LoadMode().Should().Be(expected);
    }

    [Fact]
    public void A_run_that_passes_the_gate_still_records_why()
    {
        var result = SyncValidationGate.Evaluate(
            new SyncValidationInput(SyncJobType.LicenseSkuSync, stagedRowCount: 120, lastSuccessfulRowCount: 100));

        // Notes are kept on success as well as failure, so an operator reading a run record
        // can tell the difference between "checked and fine" and "never checked".
        result.Notes.Should().NotBeNullOrWhiteSpace();
        AnyTenant.Should().NotBeEmpty();
    }
}
