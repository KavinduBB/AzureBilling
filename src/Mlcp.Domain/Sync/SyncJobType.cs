namespace Mlcp.Domain.Sync;

/// <summary>Every scheduled sync job (docs/03-architecture.md §6.1).</summary>
public enum SyncJobType
{
    Unknown = 0,
    CapabilityDiscovery = 1,
    LicenseSkuSync = 2,
    UserAssignmentSync = 3,
    UsageReportSync = 4,
    BillingSubscriptionSync = 5,
    TransactionSyncUnbilled = 6,
    TransactionSyncBilled = 7,
    InvoiceSync = 8,
    UnitPriceDerivation = 9,
    AzureCostSummarySync = 10,
    AzureCostDetailSync = 11,
    ReservationSync = 12,
    AdvisorSync = 13,
    SnapshotJob = 14,
    AssociatedTenantDiscovery = 15,
    TenantDeletion = 16,

    /// <summary>Confirms admin consent with an app-only call after the consent callback (ADR-018).</summary>
    ConsentVerification = 17,

    /// <summary>Floor-only re-probe of a tenant flagged NeedsReconsent (ADR-016 rule 5).</summary>
    ReconsentProbe = 18,
}

/// <summary>
/// How a job's staged rows relate to the live table, which determines what the validation gate
/// may legitimately assert.
/// </summary>
/// <remarks>
/// CLAUDE.md rule 6 blocks a run whose row count drops by more than half against the last
/// success. That test is only meaningful for a job that re-fetches the entire set each run. A
/// delta job stages a page of changes whose size has no relationship to the live row count, so
/// applying the same test would fail almost every run. The gate reads this mode and applies
/// the volume checks to <see cref="Full"/> jobs only; see
/// <c>Mlcp.Domain.Sync.SyncValidationGate</c>.
/// </remarks>
public enum SyncLoadMode
{
    Unknown = 0,

    /// <summary>The run fetches the complete current set. Row-count checks apply.</summary>
    Full = 1,

    /// <summary>The run fetches only changes since a cursor. Row-count checks do not apply.</summary>
    Incremental = 2,

    /// <summary>The run appends immutable facts for a period. Existing rows are never removed.</summary>
    Append = 3,
}

public static class SyncJobTypeExtensions
{
    private static readonly Dictionary<SyncJobType, SyncLoadMode> LoadModes = new()
    {
        [SyncJobType.CapabilityDiscovery] = SyncLoadMode.Full,
        [SyncJobType.LicenseSkuSync] = SyncLoadMode.Full,
        [SyncJobType.UserAssignmentSync] = SyncLoadMode.Incremental,
        [SyncJobType.UsageReportSync] = SyncLoadMode.Append,
        [SyncJobType.BillingSubscriptionSync] = SyncLoadMode.Full,
        [SyncJobType.TransactionSyncUnbilled] = SyncLoadMode.Full,
        [SyncJobType.TransactionSyncBilled] = SyncLoadMode.Append,
        [SyncJobType.InvoiceSync] = SyncLoadMode.Full,
        [SyncJobType.UnitPriceDerivation] = SyncLoadMode.Full,
        [SyncJobType.AzureCostSummarySync] = SyncLoadMode.Append,
        [SyncJobType.AzureCostDetailSync] = SyncLoadMode.Append,
        [SyncJobType.ReservationSync] = SyncLoadMode.Full,
        [SyncJobType.AdvisorSync] = SyncLoadMode.Full,
        [SyncJobType.SnapshotJob] = SyncLoadMode.Append,
        [SyncJobType.AssociatedTenantDiscovery] = SyncLoadMode.Full,
        [SyncJobType.TenantDeletion] = SyncLoadMode.Full,
    };

    /// <summary>
    /// Jobs whose healthy runs are expected to take longer than <see cref="DefaultMaxDuration"/>.
    /// Resource-level cost detail and a full user-assignment load of a very large directory
    /// both page through hundreds of thousands of rows under Microsoft's throttling limits.
    /// </summary>
    private static readonly Dictionary<SyncJobType, TimeSpan> MaxDurations = new()
    {
        [SyncJobType.UserAssignmentSync] = TimeSpan.FromHours(4),
        [SyncJobType.AzureCostDetailSync] = TimeSpan.FromHours(4),
    };

    /// <summary>The longest a healthy run of an undeclared job is expected to take.</summary>
    public static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromHours(2);

    /// <summary>
    /// How many multiples of <see cref="MaxDuration"/> a run may stay <c>Running</c> before the
    /// stuck-run sweeper treats its worker as dead (ADR-024 §7).
    /// </summary>
    public const int StuckRunMultiplier = 2;

    /// <summary>
    /// The default load mode for a job. A run may request a different mode (ADR-024 §2), for
    /// example the 30-day full resync of <see cref="SyncJobType.UserAssignmentSync"/>. Unmapped
    /// jobs fall back to <see cref="SyncLoadMode.Full"/>: the strictest gate is the safe default
    /// when a new job type has not declared its mode.
    /// </summary>
    public static SyncLoadMode LoadMode(this SyncJobType jobType)
        => LoadModes.TryGetValue(jobType, out var mode) ? mode : SyncLoadMode.Full;

    /// <summary>
    /// The longest a healthy run of this job is expected to take. Exceeding twice this marks the
    /// run abandoned; it is not a timeout applied to a live run.
    /// </summary>
    public static TimeSpan MaxDuration(this SyncJobType jobType)
        => MaxDurations.TryGetValue(jobType, out var duration) ? duration : DefaultMaxDuration;

    /// <summary>The age past which a <c>Running</c> run of this job is presumed dead.</summary>
    public static TimeSpan StuckAfter(this SyncJobType jobType)
        => jobType.MaxDuration() * StuckRunMultiplier;
}
