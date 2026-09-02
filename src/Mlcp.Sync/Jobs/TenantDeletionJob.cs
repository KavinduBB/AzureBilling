using Mlcp.Application.Onboarding;

namespace Mlcp.Sync.Jobs;

/// <summary>
/// Runs the deletion sweep on a schedule (P0-11).
/// </summary>
/// <remarks>
/// <para>
/// Deletion is time-based rather than event-based: nothing pushes us when a grace period
/// expires, so the sweep has to look. It runs hourly rather than daily so that a deletion
/// promised for a given date happens on that date rather than up to a day later, which matters
/// when the promise is "we will destroy your data on the 30th".
/// </para>
/// <para>
/// The sweep is safe to run concurrently with itself: it selects only tenants still in the
/// grace period, and deleting one is a single transaction that removes the tenant row, so a
/// second pass finds nothing to do.
/// </para>
/// </remarks>
public sealed class TenantDeletionJob : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDeletionJob> _logger;

    public TenantDeletionJob(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<TenantDeletionJob> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _timeProvider);

        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TenantDeletionService>();

                var certificates = await service.RunAsync(stoppingToken).ConfigureAwait(false);

                if (certificates.Count > 0)
                {
                    _logger.LogWarning("Deletion sweep issued {Count} certificate(s).", certificates.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed sweep must not stop the worker; the next one retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Deletion sweep failed. Retrying on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
