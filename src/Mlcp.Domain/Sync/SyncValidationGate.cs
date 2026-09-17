using System.Globalization;

namespace Mlcp.Domain.Sync;

/// <summary>
/// The previous successful run a new run is compared against (ADR-024 §1–2): the last
/// <see cref="SyncRunStatus.Succeeded"/> run of the same tenant, job, load mode and period.
/// </summary>
/// <param name="SyncRunId">The baseline run, named in the gate notes.</param>
/// <param name="StagedRowCount">What that run staged. Never the rows it merged.</param>
/// <param name="StartedUtc">
/// When that run started. An invoice recorded after this instant explains a drop, because the
/// baseline may already have been fetched before the invoice moved charges out of the period.
/// </param>
public sealed record SyncGateBaseline(Guid SyncRunId, int StagedRowCount, DateTimeOffset StartedUtc);

/// <summary>The operator approval a run carries into the gate (ADR-024 §4).</summary>
public sealed record SyncGateOverrideGrant(Guid SyncGateOverrideId, string ApprovedBy, string Reason);

/// <summary>What the pipeline observed in staging, ready for the gate to judge.</summary>
/// <param name="LoadMode">The run's mode; volume checks apply to <see cref="SyncLoadMode.Full"/> only.</param>
/// <param name="StagedRowCount">Every row in staging for this run, including adopted rows.</param>
/// <param name="Baseline">
/// The comparable previous success, or null when this is the first run of this job, mode and
/// period. A first run has no baseline and cannot fail a comparison.
/// </param>
/// <param name="FieldViolations">
/// Rows that failed a required-field or range check, described one per entry. Any entry fails
/// the run outright regardless of load mode or override.
/// </param>
public sealed record SyncValidationInput(
    SyncLoadMode LoadMode,
    int StagedRowCount,
    SyncGateBaseline? Baseline,
    IReadOnlyCollection<string> FieldViolations)
{
    public SyncValidationInput(SyncLoadMode loadMode, int stagedRowCount, SyncGateBaseline? baseline)
        : this(loadMode, stagedRowCount, baseline, [])
    {
    }

    /// <summary>The run's billing period, or null for jobs that are not period-scoped.</summary>
    public string? PeriodKey { get; init; }

    /// <summary>
    /// True when <c>InvoiceSync</c> recorded an invoice for <see cref="PeriodKey"/> since the
    /// baseline run started. Only consulted for a period-scoped drop (ADR-024 §3).
    /// </summary>
    public bool InvoiceIssuedSinceBaseline { get; init; }

    /// <summary>An unconsumed, unexpired operator override for this tenant and job, if any.</summary>
    public SyncGateOverrideGrant? Override { get; init; }
}

/// <summary>Why the gate reached its verdict. Stable names: they appear in logs and alerts.</summary>
public enum SyncGateDecision
{
    Unknown = 0,

    /// <summary>A required-field or range check failed. Always blocks.</summary>
    FieldViolations = 1,

    /// <summary>Incremental or append load; volume checks do not apply (ADR-013).</summary>
    VolumeNotApplicable = 2,

    /// <summary>No earlier success for this job, mode and period, or its baseline was empty.</summary>
    NoBaseline = 3,

    /// <summary>At least half the baseline was staged.</summary>
    WithinTolerance = 4,

    /// <summary>A full load staged nothing against a non-empty baseline. Blocks.</summary>
    BlockedEmpty = 5,

    /// <summary>A full load staged less than half its baseline. Blocks.</summary>
    BlockedDrop = 6,

    /// <summary>A same-period drop explained by an invoice issued since the baseline.</summary>
    AllowedInvoiceIssued = 7,

    /// <summary>A drop waived by an operator override.</summary>
    AllowedByOverride = 8,
}

/// <param name="Passed">When false, the MERGE must not run and live tables stay untouched.</param>
/// <param name="Notes">Recorded on the <see cref="SyncRun"/> whether the gate passed or failed.</param>
/// <param name="Decision">The rule that decided the verdict.</param>
/// <param name="ConsumesOverride">
/// True when the run must consume <see cref="SyncValidationInput.Override"/> in its merge
/// transaction. An override applies to the next full load that reaches the merge, whether or
/// not that load needed it: an approval left lying around would otherwise waive a genuine
/// truncation hours later that nobody reviewed.
/// </param>
public sealed record SyncValidationResult(bool Passed, string Notes, SyncGateDecision Decision, bool ConsumesOverride = false)
{
    /// <summary>True when the verdict came from the volume rule rather than field checks.</summary>
    public bool IsVolumeBlock => Decision is SyncGateDecision.BlockedEmpty or SyncGateDecision.BlockedDrop;
}

/// <summary>
/// The gate that stands between staging and the live tables. It exists to make a bad Microsoft
/// response — an empty page, a truncated result, a partial outage — unable to destroy a
/// tenant's data (CLAUDE.md rule 6, ADR-013, ADR-024).
/// </summary>
public static class SyncValidationGate
{
    // Notes are read by operators and matched by alerts, so they must not vary with the
    // worker's locale.
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Minimum share of the baseline's staged row count that a full load must reach. A drop
    /// past this is treated as a partial response rather than a real deletion.
    /// </summary>
    public const double MinimumRetainedFraction = 0.5;

    /// <summary>
    /// Judges a staged run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Volume checks apply only to <see cref="SyncLoadMode.Full"/> runs: an incremental run
    /// legitimately stages zero rows when nothing changed, and an append run legitimately
    /// stages zero rows when a period produced no new facts (ADR-013).
    /// </para>
    /// <para>
    /// A blocked drop has two escape hatches, in this order: an operator override, then (for a
    /// period-scoped run only) an invoice issued since the baseline. Neither can waive a field
    /// violation, which means the rows themselves are wrong rather than fewer.
    /// </para>
    /// <para>
    /// The gate is pure. The pipeline looks up the invoice only when a period-scoped drop is
    /// found (<see cref="SyncValidationResult.IsVolumeBlock"/>) and evaluates again, so the
    /// common case costs no extra query.
    /// </para>
    /// </remarks>
    public static SyncValidationResult Evaluate(SyncValidationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.FieldViolations.Count > 0)
        {
            return Fail(
                SyncGateDecision.FieldViolations,
                string.Create(Inv, $"{input.FieldViolations.Count} row(s) failed field validation: {string.Join("; ", input.FieldViolations.Take(10))}"));
        }

        var mode = input.LoadMode;

        if (mode is not SyncLoadMode.Full)
        {
            return Pass(
                SyncGateDecision.VolumeNotApplicable,
                string.Create(Inv, $"{mode} load: {input.StagedRowCount} row(s) staged; volume checks not applicable."),
                consumesOverride: false);
        }

        var consumesOverride = input.Override is not null;
        var period = input.PeriodKey is null ? string.Empty : string.Create(Inv, $" for period {input.PeriodKey}");

        if (input.Baseline is not { } baseline)
        {
            return Pass(
                SyncGateDecision.NoBaseline,
                string.Create(Inv, $"Full load: {input.StagedRowCount} row(s) staged; first successful run{period}, so no baseline to compare against.")
                + OverrideNote(input.Override),
                consumesOverride);
        }

        if (baseline.StagedRowCount == 0)
        {
            return Pass(
                SyncGateDecision.NoBaseline,
                string.Create(Inv, $"Full load: {input.StagedRowCount} row(s) staged; baseline run {baseline.SyncRunId}{period} staged 0 rows, so there is nothing to lose.")
                + OverrideNote(input.Override),
                consumesOverride);
        }

        var threshold = baseline.StagedRowCount * MinimumRetainedFraction;

        if (input.StagedRowCount >= threshold)
        {
            return Pass(
                SyncGateDecision.WithinTolerance,
                string.Create(Inv, $"Full load: {input.StagedRowCount} row(s) staged against a baseline of {baseline.StagedRowCount} from run {baseline.SyncRunId}{period}.")
                + OverrideNote(input.Override),
                consumesOverride);
        }

        var drop = input.StagedRowCount == 0
            ? string.Create(Inv, $"Full load staged 0 rows but baseline run {baseline.SyncRunId}{period} staged {baseline.StagedRowCount}. Treating as a partial response")
            : string.Create(Inv, $"Full load staged {input.StagedRowCount} rows against a baseline of {baseline.StagedRowCount} from run {baseline.SyncRunId}{period}, below the {MinimumRetainedFraction:P0} floor of {threshold:F1}");

        if (input.Override is { } grant)
        {
            return Pass(
                SyncGateDecision.AllowedByOverride,
                string.Create(Inv, $"{drop}; allowed by operator override {grant.SyncGateOverrideId} approved by {grant.ApprovedBy}: {grant.Reason}"),
                consumesOverride: true);
        }

        if (input.PeriodKey is not null && input.InvoiceIssuedSinceBaseline)
        {
            return Pass(
                SyncGateDecision.AllowedInvoiceIssued,
                string.Create(Inv, $"{drop}; allowed because an invoice for period {input.PeriodKey} was recorded after the baseline run started."),
                consumesOverride: false);
        }

        return Fail(
            input.StagedRowCount == 0 ? SyncGateDecision.BlockedEmpty : SyncGateDecision.BlockedDrop,
            string.Create(Inv, $"{drop}. Live data left untouched."));
    }

    private static string OverrideNote(SyncGateOverrideGrant? grant)
        => grant is null
            ? string.Empty
            : string.Create(Inv, $" Operator override {grant.SyncGateOverrideId} (approved by {grant.ApprovedBy}: {grant.Reason}) consumed by this run.");

    private static SyncValidationResult Pass(SyncGateDecision decision, string notes, bool consumesOverride)
        => new(true, notes, decision, consumesOverride);

    private static SyncValidationResult Fail(SyncGateDecision decision, string notes)
        => new(false, notes, decision);

}
