using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Tenancy;
using Mlcp.Persistence.Rls;

namespace Mlcp.Persistence.Stores;

/// <summary>
/// Cross-tenant store for the deletion sweep.
/// </summary>
/// <remarks>
/// Runs under the system context, which is the only way to see tenants other than one's own. It
/// is registered exclusively in the sync worker; the web application never resolves it.
/// </remarks>
public sealed class TenantDeletionStore : ITenantDeletionStore
{
    private readonly ISystemDbContextFactory _contextFactory;

    public TenantDeletionStore(ISystemDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <summary>
    /// Every tenant-scoped table, children first and <c>Tenant</c> last. A unit test requires
    /// this to cover every table in <see cref="TenantRlsScript.TenantScopedTables"/>, so a new
    /// table cannot be left behind by deletion.
    /// </summary>
    /// <remarks>
    /// The audit log goes first, in a single statement. Its self-reference from outcome row to
    /// attempt row is checked at the end of that statement, so no finer ordering is needed.
    /// </remarks>
    public static IReadOnlyList<string> DeletionOrder { get; } =
    [
        DeletionCertificate.AuditLogTableName,
        "SyncGateOverride",
        "SyncRun",
        "PendingConsentRequest",
        "OnboardingStep",
        "AppUser",
        "TenantCapabilityProfile",
        "Tenant",
    ];

    public async Task<IReadOnlyList<Tenant>> FindTenantsDueForDeletionAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            return await context.Tenants
                .AsNoTracking()
                .Where(t => t.Status == TenantStatus.GracePeriod
                    && t.DeleteScheduledUtc != null
                    && t.DeleteScheduledUtc <= asOfUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes every row for a tenant and writes its certificate, in one transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order matters:
    /// </para>
    /// <list type="number">
    /// <item><b>Lock and re-check.</b> The tenant row is read with <c>UPDLOCK, HOLDLOCK</c> and
    /// checked with <see cref="Tenant.IsDueForDeletion"/>. A tenant that reconnected after the
    /// candidate query is left alone. A second sweep racing on the same tenant waits on the lock,
    /// then finds no row and does nothing, so it cannot issue a second certificate.</item>
    /// <item><b>Digest.</b> The audit rows are read in id order under <c>HOLDLOCK</c>, which
    /// takes range locks, so no audit row can be added between the digest and the delete. The
    /// delete count is checked against the digested count, and any difference aborts.</item>
    /// <item><b>Delete.</b> Set-based DELETEs in <see cref="DeletionOrder"/>, measured rather
    /// than assumed. Nothing cascades from <c>Tenant</c> to the audit log (ADR-019), so a
    /// forgotten table fails the final delete instead of vanishing uncounted.</item>
    /// <item><b>Certify and commit.</b> The certificate is inserted and the transaction
    /// committed together, so the certificate exists if and only if the data is gone.</item>
    /// </list>
    /// <para>
    /// The work runs inside the context's execution strategy. A strategy that retries would
    /// otherwise refuse a transaction the application started itself.
    /// </para>
    /// </remarks>
    public async Task<DeletionCertificate?> DeleteAllTenantDataAsync(
        Guid tenantId,
        DateTimeOffset asOfUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var context = _contextFactory.CreateDbContext();

        await using (context.ConfigureAwait(false))
        {
            var strategy = context.Database.CreateExecutionStrategy();

            return await strategy
                .ExecuteAsync(
                    ct => DeleteInTransactionAsync(context, tenantId, asOfUtc, correlationId, ct),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<DeletionCertificate?> DeleteInTransactionAsync(
        MlcpDbContext context,
        Guid tenantId,
        DateTimeOffset asOfUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        // A retried attempt must not carry a certificate tracked by the attempt that failed.
        context.ChangeTracker.Clear();

        var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        await using (transaction.ConfigureAwait(false))
        {
            var tenant = await context.Tenants
                .FromSql($"SELECT * FROM [dbo].[Tenant] WITH (UPDLOCK, HOLDLOCK) WHERE [TenantId] = {tenantId}")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (tenant is null
                || !tenant.IsDueForDeletion(asOfUtc)
                || tenant.DisconnectedUtc is not { } disconnectedUtc)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            using var digest = new AuditLogDigest();

            var auditRows = context.AuditLogs
                .FromSql($"SELECT * FROM [dbo].[AuditLog] WITH (HOLDLOCK) WHERE [TenantId] = {tenantId}")
                .AsNoTracking()
                .OrderBy(a => a.AuditLogId)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false);

            await foreach (var row in auditRows)
            {
                digest.Append(row);
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var table in DeletionOrder)
            {
                TenantRlsScript.EnsurePlainIdentifier(table);

                var sql = "DELETE FROM [dbo].[" + table + "] WHERE [TenantId] = @tenantId;";

                counts[table] = await context.Database
                    .ExecuteSqlRawAsync(sql, [new SqlParameter("@tenantId", tenantId)], cancellationToken)
                    .ConfigureAwait(false);
            }

            if (counts[DeletionCertificate.AuditLogTableName] != digest.RowCount)
            {
                throw new InvalidOperationException(
                    $"Audit rows changed during deletion of tenant {tenantId}: digested {digest.RowCount}, "
                    + $"deleted {counts[DeletionCertificate.AuditLogTableName]}. Rolled back.");
            }

            if (counts["Tenant"] != 1)
            {
                throw new InvalidOperationException(
                    $"Expected to delete one tenant row for {tenantId} but deleted {counts["Tenant"]}. Rolled back.");
            }

            var certificate = DeletionCertificate.Issue(
                tenantId,
                tenant.DisplayName,
                disconnectedUtc,
                counts,
                digest.Finish(),
                correlationId,
                asOfUtc);

            context.DeletionCertificates.Add(certificate);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return certificate;
        }
    }
}
