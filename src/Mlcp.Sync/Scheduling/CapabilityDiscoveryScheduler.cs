using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.Persistence;

namespace Mlcp.Sync.Scheduling;

/// <summary>
/// Enqueues capability discovery for tenants whose profile is due for a refresh.
/// </summary>
/// <remarks>
/// <para>
/// Capability is not static. Customers grant an Azure role weeks after connecting, billing roles
/// get removed during a reorganisation, and Microsoft migrates a MOSA account to MCA at renewal.
/// A profile that is never re-checked would leave a customer looking at a remediation prompt for
/// something they have already done, or at data we can no longer read.
/// </para>
/// <para>
/// Only tenants eligible for sync are considered: one in the grace period or awaiting re-consent
/// must not be probed, because calling Microsoft for a customer who has disconnected is exactly
/// what we told them we would stop doing.
/// </para>
/// </remarks>
public sealed class CapabilityDiscoveryScheduler : BackgroundService
{
    /// <summary>How often the scheduler looks for due work.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    /// <summary>Cap per sweep, so one pass cannot flood the queue after an outage.</summary>
    private const int MaxTenantsPerSweep = 200;

    private readonly ISystemDbContextFactory _contextFactory;
    private readonly ISyncJobDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CapabilityDiscoveryScheduler> _logger;

    public CapabilityDiscoveryScheduler(
        ISystemDbContextFactory contextFactory,
        ISyncJobDispatcher dispatcher,
        TimeProvider timeProvider,
        ILogger<CapabilityDiscoveryScheduler> logger)
    {
        _contextFactory = contextFactory;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _timeProvider);

        // One pass immediately at startup, then on the timer: a worker that has just been
        // deployed should not wait a quarter of an hour before doing anything.
        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The scheduler must survive a bad sweep and try again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Capability discovery sweep failed. Retrying on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            var due = await context.TenantCapabilityProfiles
                .AsNoTracking()
                .Where(p => p.NextProfileUtc <= now)
                .Join(
                    context.Tenants.Where(t =>
                        t.Status == TenantStatus.Provisioning || t.Status == TenantStatus.Active),
                    profile => profile.TenantId,
                    tenant => tenant.TenantId,
                    (profile, tenant) => tenant.TenantId)
                .OrderBy(id => id)
                .Take(MaxTenantsPerSweep)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (due.Count == 0)
            {
                return;
            }

            foreach (var tenantId in due)
            {
                // The slot is the tenant's staggered position in the current hour, which is also
                // what makes the message id stable and duplicate detection effective.
                var slot = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)
                    .AddMinutes(SyncSchedule.StaggerOffsetMinutes(tenantId));

                await _dispatcher.DispatchAsync(
                    new SyncJobMessage(
                        tenantId,
                        SyncJobType.CapabilityDiscovery,
                        slot,
                        Guid.NewGuid().ToString("N")),
                    cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Enqueued capability discovery for {Count} tenant(s).", due.Count);
        }
    }
}
