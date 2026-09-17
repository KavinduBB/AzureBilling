using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Sync;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.Persistence;

namespace Mlcp.Sync.Scheduling;

/// <summary>
/// Enqueues capability discovery for tenants whose profile is due, and for tenants stuck in
/// Provisioning without one.
/// </summary>
/// <remarks>
/// <para>
/// Due profiles are taken oldest-due first, so a large fleet cannot starve the tail. Each queued
/// profile is leased (its <c>NextProfileUtc</c> moved past the queued slot) so the next pass does
/// not pick it again; discovery replaces the lease with the real next run. The tenant's stagger is
/// applied through the message's scheduled enqueue time, and the slot is the deduplication key.
/// </para>
/// <para>
/// Only sync-eligible tenants are considered: calling Microsoft for a tenant that disconnected or
/// needs re-consent is exactly what we said we would stop doing.
/// </para>
/// </remarks>
public sealed class DiscoverySchedulePass
{
    public const int MaxTenantsPerPass = 200;

    /// <summary>A Provisioning tenant this long after consent with no profile is considered stuck.</summary>
    public static TimeSpan StuckProvisioningAfter { get; } = TimeSpan.FromMinutes(15);

    private readonly ISystemDbContextFactory _contextFactory;
    private readonly ISyncJobEnqueuer _enqueuer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiscoverySchedulePass> _logger;

    public DiscoverySchedulePass(
        ISystemDbContextFactory contextFactory,
        ISyncJobEnqueuer enqueuer,
        TimeProvider timeProvider,
        ILogger<DiscoverySchedulePass> logger)
    {
        _contextFactory = contextFactory;
        _enqueuer = enqueuer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var stuckCutoff = now - StuckProvisioningAfter;
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            var stuck = await context.Tenants
                .Where(t => t.Status == TenantStatus.Provisioning
                    && (t.ConsentGrantedUtc == null || t.ConsentGrantedUtc <= stuckCutoff)
                    && !context.TenantCapabilityProfiles.Any(p => p.TenantId == t.TenantId))
                .OrderBy(t => t.ConsentGrantedUtc)
                .Select(t => t.TenantId)
                .Take(MaxTenantsPerPass)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var due = await context.TenantCapabilityProfiles
                .Where(p => p.NextProfileUtc <= now
                    && context.Tenants.Any(t => t.TenantId == p.TenantId
                        && (t.Status == TenantStatus.Provisioning || t.Status == TenantStatus.Active)))
                .OrderBy(p => p.NextProfileUtc)
                .Take(MaxTenantsPerPass - stuck.Count)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var tenantId in stuck)
            {
                // Stuck onboarding is not staggered: a customer is waiting for their first data.
                var profile = TenantCapabilityProfile.Undiscovered(tenantId, now);
                profile.LeaseUntil(now + SyncSchedule.DiscoveryLease, now);
                context.TenantCapabilityProfiles.Add(profile);

                await EnqueueAsync(tenantId, $"stuck:{now:yyyyMMddHHmm}", notBeforeUtc: null, cancellationToken).ConfigureAwait(false);
            }

            foreach (var profile in due)
            {
                var slot = SyncSchedule.NextStaggeredSlot(profile.TenantId, now);
                profile.LeaseUntil(slot + SyncSchedule.DiscoveryLease, now);

                await EnqueueAsync(profile.TenantId, SyncSchedule.SlotKey(slot), slot, cancellationToken).ConfigureAwait(false);
            }

            if (stuck.Count + due.Count == 0)
            {
                return 0;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex)
            {
                // A discovery finished concurrently and moved the profile itself; the enqueued
                // message is deduplicated by slot, so nothing is lost.
                _logger.LogWarning(ex, "Could not lease every discovered profile; the next pass will reconcile.");
            }

            _logger.LogInformation(
                "Enqueued capability discovery for {DueCount} due and {StuckCount} stuck tenant(s).",
                due.Count,
                stuck.Count);

            return stuck.Count + due.Count;
        }
    }

    private Task EnqueueAsync(Guid tenantId, string key, DateTimeOffset? notBeforeUtc, CancellationToken cancellationToken)
        => _enqueuer.EnqueueAsync(
            tenantId,
            SyncJobType.CapabilityDiscovery,
            key,
            notBeforeUtc,
            Guid.NewGuid().ToString("N"),
            cancellationToken);
}

/// <summary>
/// Enqueues the floor-only re-probe for tenants in NeedsReconsent whose next probe is due
/// (ADR-016 rule 5: +1 h, +6 h, +24 h, daily for 30 days, then weekly).
/// </summary>
/// <remarks>
/// The tenant's schedule only moves when the probe runs, so the deduplication key is derived from
/// the due time: a pass that runs again before the probe has been processed re-sends the same
/// message id, and the consumer re-checks the status before probing.
/// </remarks>
public sealed class ReconsentProbeSchedulePass
{
    public const int MaxTenantsPerPass = 200;

    private readonly ISystemDbContextFactory _contextFactory;
    private readonly ISyncJobEnqueuer _enqueuer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReconsentProbeSchedulePass> _logger;

    public ReconsentProbeSchedulePass(
        ISystemDbContextFactory contextFactory,
        ISyncJobEnqueuer enqueuer,
        TimeProvider timeProvider,
        ILogger<ReconsentProbeSchedulePass> logger)
    {
        _contextFactory = contextFactory;
        _enqueuer = enqueuer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// <c>reconsent-probe:{tid}:{5-minute slot}</c>, the convention the web host's "Check again"
    /// button uses, so a scheduled probe and a manual one in the same slot run once.
    /// </summary>
    public static string DeduplicationKey(Guid tenantId, DateTimeOffset dueUtc)
    {
        var utc = dueUtc.ToUniversalTime();
        var slot = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute - (utc.Minute % 5), 0, TimeSpan.Zero);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"reconsent-probe:{tenantId:D}:{slot:yyyyMMddHHmm}");
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            var due = await context.Tenants
                .AsNoTracking()
                .Where(t => t.Status == TenantStatus.NeedsReconsent
                    && t.NextReconsentProbeUtc != null
                    && t.NextReconsentProbeUtc <= now)
                .OrderBy(t => t.NextReconsentProbeUtc)
                .Take(MaxTenantsPerPass)
                .Select(t => new { t.TenantId, t.NextReconsentProbeUtc })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var tenant in due)
            {
                await _enqueuer.EnqueueAsync(
                    tenant.TenantId,
                    SyncJobType.ReconsentProbe,
                    DeduplicationKey(tenant.TenantId, tenant.NextReconsentProbeUtc!.Value),
                    notBeforeUtc: null,
                    Guid.NewGuid().ToString("N"),
                    cancellationToken).ConfigureAwait(false);
            }

            if (due.Count > 0)
            {
                _logger.LogInformation("Enqueued re-consent probes for {Count} tenant(s).", due.Count);
            }

            return due.Count;
        }
    }
}

/// <summary>Runs the scheduling passes on a timer.</summary>
public sealed class SyncScheduler : BackgroundService
{
    /// <summary>How often the scheduler looks for due work.</summary>
    public static TimeSpan SweepInterval { get; } = TimeSpan.FromMinutes(5);

    private readonly DiscoverySchedulePass _discovery;
    private readonly ReconsentProbeSchedulePass _reconsent;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncScheduler> _logger;

    public SyncScheduler(
        DiscoverySchedulePass discovery,
        ReconsentProbeSchedulePass reconsent,
        TimeProvider timeProvider,
        ILogger<SyncScheduler> logger)
    {
        _discovery = discovery;
        _reconsent = reconsent;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _timeProvider);

        // One pass at startup, then on the timer.
        do
        {
            await RunPassAsync("discovery", _discovery.RunAsync, stoppingToken).ConfigureAwait(false);
            await RunPassAsync("re-consent probe", _reconsent.RunAsync, stoppingToken).ConfigureAwait(false);
        }
        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunPassAsync(string name, Func<CancellationToken, Task<int>> pass, CancellationToken stoppingToken)
    {
        try
        {
            await pass(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // The scheduler must survive a bad pass and try again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "The {Pass} scheduling pass failed. Retrying on the next interval.", name);
        }
    }
}
