using System.Globalization;

namespace Mlcp.Domain.Sync;

/// <summary>What the pipeline observed in staging, ready for the gate to judge.</summary>
/// <param name="JobType">Determines which checks apply, via its <see cref="SyncLoadMode"/>.</param>
/// <param name="StagedRowCount">Rows fetched into staging for this run.</param>
/// <param name="LastSuccessfulRowCount">
/// Rows processed by the previous successful run of the same job, or null when this is the
/// first run. A first run has no baseline and cannot fail a comparison.
/// </param>
/// <param name="FieldViolations">
/// Rows that failed a required-field or range check, described one per entry. Any entry fails
/// the run outright regardless of load mode.
/// </param>
public sealed record SyncValidationInput(
    SyncJobType JobType,
    int StagedRowCount,
    int? LastSuccessfulRowCount,
    IReadOnlyCollection<string> FieldViolations)
{
    public SyncValidationInput(SyncJobType jobType, int stagedRowCount, int? lastSuccessfulRowCount)
        : this(jobType, stagedRowCount, lastSuccessfulRowCount, [])
    {
    }
}

/// <param name="Passed">When false, the MERGE must not run and live tables stay untouched.</param>
/// <param name="Notes">Recorded on the <see cref="SyncRun"/> whether the gate passed or failed.</param>
public sealed record SyncValidationResult(bool Passed, string Notes)
{
    public static SyncValidationResult Pass(string notes) => new(true, notes);

    public static SyncValidationResult Fail(string notes) => new(false, notes);
}

/// <summary>
/// The gate that stands between staging and the live tables. It exists to make a bad Microsoft
/// response — an empty page, a truncated result, a partial outage — unable to destroy a
/// tenant's data (CLAUDE.md rule 6, docs/03-architecture.md §6.2 step 3).
/// </summary>
public static class SyncValidationGate
{
    /// <summary>
    /// Minimum share of the previous successful run's row count that a full load must reach.
    /// A drop past this is treated as a partial response rather than a real deletion.
    /// </summary>
    public const double MinimumRetainedFraction = 0.5;

    /// <summary>
    /// Judges a staged run. Volume checks apply only to <see cref="SyncLoadMode.Full"/> jobs:
    /// an incremental job legitimately stages zero rows when nothing changed, and an append job
    /// legitimately stages zero rows when a period produced no new facts. Applying the
    /// full-load rule to either would fail almost every run and train operators to ignore the
    /// gate, which is worse than not having one.
    /// </summary>
    public static SyncValidationResult Evaluate(SyncValidationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.FieldViolations.Count > 0)
        {
            return SyncValidationResult.Fail(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{input.FieldViolations.Count} row(s) failed field validation: {string.Join("; ", input.FieldViolations.Take(10))}"));
        }

        var mode = input.JobType.LoadMode();

        if (mode is not SyncLoadMode.Full)
        {
            return SyncValidationResult.Pass(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{mode} load: {input.StagedRowCount} row(s) staged; volume checks not applicable."));
        }

        if (input.LastSuccessfulRowCount is not { } baseline || baseline == 0)
        {
            return SyncValidationResult.Pass(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Full load: {input.StagedRowCount} row(s) staged; no non-empty baseline to compare against."));
        }

        if (input.StagedRowCount == 0)
        {
            return SyncValidationResult.Fail(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Full load staged 0 rows but the last successful run processed {baseline}. Treating as a partial response; live data left untouched."));
        }

        var threshold = baseline * MinimumRetainedFraction;

        if (input.StagedRowCount < threshold)
        {
            return SyncValidationResult.Fail(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Full load staged {input.StagedRowCount} rows against a baseline of {baseline}, below the {MinimumRetainedFraction:P0} floor of {threshold:F1}. Live data left untouched."));
        }

        return SyncValidationResult.Pass(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Full load: {input.StagedRowCount} row(s) staged against a baseline of {baseline}."));
    }
}
