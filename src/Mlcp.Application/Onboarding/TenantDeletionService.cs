using Microsoft.Extensions.Logging;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Onboarding;

/// <summary>Cross-tenant persistence for the deletion sweep.</summary>
/// <remarks>
/// Every method here reads or writes across tenants by necessity, which is why the interface is
/// separate from <see cref="IOnboardingRepository"/> rather than bolted onto it: a type that can
/// do this should be obvious in a constructor and awkward to obtain by accident.
/// </remarks>
public interface ITenantDeletionStore
{
    /// <summary>Tenants whose grace period has elapsed and whose data is now due for destruction.</summary>
    Task<IReadOnlyList<Tenant>> FindTenantsDueForDeletionAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every row belonging to <paramref name="tenantId"/> and returns the number removed
    /// per table. Must be atomic: a partial deletion is worse than none, because it leaves data
    /// we have already told the customer is gone.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> DeleteAllTenantDataAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddCertificateAsync(DeletionCertificate certificate, CancellationToken cancellationToken);
}

/// <summary>
/// Destroys the data of tenants whose 30-day grace period has expired, and records that it did
/// (P0-11, docs/03-architecture.md §8).
/// </summary>
/// <remarks>
/// The sweep processes one tenant per transaction rather than all of them in one. A single
/// failure then costs one tenant's deletion, retried on the next run, instead of rolling back a
/// batch and leaving every customer in the batch believing their data was destroyed when it was
/// not.
/// </remarks>
public sealed class TenantDeletionService
{
    private readonly ITenantDeletionStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDeletionService> _logger;

    public TenantDeletionService(
        ITenantDeletionStore store,
        TimeProvider timeProvider,
        ILogger<TenantDeletionService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs one sweep and returns the certificates issued.</summary>
    public async Task<IReadOnlyList<DeletionCertificate>> RunAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var due = await _store.FindTenantsDueForDeletionAsync(now, cancellationToken).ConfigureAwait(false);

        if (due.Count == 0)
        {
            return [];
        }

        _logger.LogWarning("{Count} tenant(s) are due for permanent deletion.", due.Count);

        var certificates = new List<DeletionCertificate>(due.Count);

        foreach (var tenant in due)
        {
            var correlationId = Guid.NewGuid().ToString("N");

            try
            {
                var counts = await _store.DeleteAllTenantDataAsync(tenant.TenantId, cancellationToken)
                    .ConfigureAwait(false);

                var certificate = DeletionCertificate.Issue(
                    tenant.TenantId,
                    tenant.DisplayName,
                    tenant.DeleteScheduledUtc ?? now,
                    counts,
                    correlationId,
                    now);

                await _store.AddCertificateAsync(certificate, cancellationToken).ConfigureAwait(false);

                certificates.Add(certificate);

                _logger.LogWarning(
                    "Deleted all data for tenant {TenantId}: {RowsDeleted} row(s) across {TableCount} table(s). Certificate {CertificateId}.",
                    tenant.TenantId,
                    certificate.RowsDeleted,
                    counts.Count,
                    certificate.DeletionCertificateId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // One tenant failing must not abandon the rest of the sweep.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(
                    ex,
                    "Failed to delete data for tenant {TenantId}. It remains scheduled and will be retried.",
                    tenant.TenantId);
            }
        }

        return certificates;
    }
}
