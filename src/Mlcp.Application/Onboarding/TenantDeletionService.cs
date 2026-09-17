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
    /// <summary>
    /// Tenants whose grace period has elapsed. This is only a candidate list: eligibility is
    /// checked again inside the deletion transaction.
    /// </summary>
    Task<IReadOnlyList<Tenant>> FindTenantsDueForDeletionAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every row belonging to <paramref name="tenantId"/> and writes the deletion
    /// certificate, all in one transaction (ADR-019).
    /// </summary>
    /// <remarks>
    /// Implementations must:
    /// <list type="number">
    /// <item>lock the tenant row and check again that it is in the grace period and due as of
    /// <paramref name="asOfUtc"/>;</item>
    /// <item>take the audit-log digest;</item>
    /// <item>delete every tenant-scoped table;</item>
    /// <item>insert the certificate;</item>
    /// <item>commit.</item>
    /// </list>
    /// A partial deletion is worse than none, because it leaves data we have already told the
    /// customer is gone. A certificate without the deletion, or the reverse, is a false record.
    /// </remarks>
    /// <returns>
    /// The certificate written, or null when the tenant was no longer eligible, for example
    /// because it reconnected or another sweep already deleted it. Nothing is changed in that case.
    /// </returns>
    Task<DeletionCertificate?> DeleteAllTenantDataAsync(
        Guid tenantId,
        DateTimeOffset asOfUtc,
        string correlationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Destroys the data of tenants whose 30-day grace period has expired, and records that it did
/// (P0-11, docs/03-architecture.md §8, ADR-019).
/// </summary>
/// <remarks>
/// <para>
/// The sweep processes one tenant per transaction rather than all of them in one. A single
/// failure then costs one tenant's deletion, retried on the next run, instead of rolling back a
/// batch and leaving every customer in the batch believing their data was destroyed when it was
/// not.
/// </para>
/// <para>
/// The certificate is written by the store inside the deletion transaction, never here
/// afterwards. A crash between the two steps could otherwise leave data deleted with no
/// record, or, if the order were reversed, a record for data that still exists.
/// </para>
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
        var candidates = await _store.FindTenantsDueForDeletionAsync(now, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return [];
        }

        _logger.LogInformation("{Count} tenant(s) are candidates for permanent deletion.", candidates.Count);

        var certificates = new List<DeletionCertificate>(candidates.Count);

        foreach (var tenant in candidates)
        {
            if (!tenant.IsDueForDeletion(now))
            {
                _logger.LogWarning(
                    "Tenant {TenantId} was returned as due for deletion but is not due as of {AsOfUtc}; skipped.",
                    tenant.TenantId,
                    now);
                continue;
            }

            var correlationId = Guid.NewGuid().ToString("N");

            try
            {
                var certificate = await _store
                    .DeleteAllTenantDataAsync(tenant.TenantId, now, correlationId, cancellationToken)
                    .ConfigureAwait(false);

                if (certificate is null)
                {
                    // Not an error: the tenant reconnected after the candidate query, or another
                    // sweep deleted it first. Nothing was deleted by this run.
                    _logger.LogInformation(
                        "Tenant {TenantId} was no longer eligible for deletion when locked; nothing was deleted. Correlation {CorrelationId}.",
                        tenant.TenantId,
                        correlationId);
                    continue;
                }

                certificates.Add(certificate);

                _logger.LogWarning(
                    "Permanently deleted tenant {TenantId}: {RowsDeleted} row(s). Certificate {CertificateId} (audit digest {AuditLogSha256}) was written in the same transaction. Correlation {CorrelationId}.",
                    tenant.TenantId,
                    certificate.RowsDeleted,
                    certificate.DeletionCertificateId,
                    certificate.AuditLogSha256,
                    correlationId);
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
                    // Honest about the one ambiguous case: a failure during commit may still have
                    // committed. The next sweep settles it, because a deleted tenant is no longer
                    // a candidate and its certificate is then already in place.
                    "Deleting tenant {TenantId} failed. The transaction was rolled back unless the failure occurred during commit; the tenant will be re-checked on the next sweep. Correlation {CorrelationId}.",
                    tenant.TenantId,
                    correlationId);
            }
        }

        return certificates;
    }
}
