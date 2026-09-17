namespace Mlcp.Domain.Tenancy;

/// <summary>
/// A dated record that a tenant's data was destroyed (P0-11, ADR-019).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <em>not</em> tenant-scoped, and deliberately outside row-level security. A
/// certificate that lived in the tenant's own rows would be deleted along with them, which
/// would leave us unable to answer the one question a departed customer or a regulator actually
/// asks: can you show that you deleted it.
/// </para>
/// <para>
/// It holds no personal data and no customer content. It records only:
/// </para>
/// <list type="bullet">
/// <item>the tenant id, which identifies an organisation rather than a person and is the only
/// thing that makes the record verifiable;</item>
/// <item>when the tenant disconnected and when its data was deleted;</item>
/// <item>how many rows went from each table;</item>
/// <item>a SHA-256 digest of the audit log as it stood just before the purge.</item>
/// </list>
/// <para>
/// It is written in the same transaction as the deletion, so it exists exactly when the data
/// does not.
/// </para>
/// </remarks>
public class DeletionCertificate
{
    /// <summary>Key under which <see cref="RowCountsByTable"/> records the audit-log count.</summary>
    public const string AuditLogTableName = "AuditLog";

    public Guid DeletionCertificateId { get; private set; }

    /// <summary>The Entra tenant whose data was destroyed.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Display name at the time of deletion, so the record is legible to a human.</summary>
    public string TenantDisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// When the customer disconnected, which started the grace period. Together with
    /// <see cref="TenantId"/> it identifies one period of connection, so a tenant that
    /// reconnects and later leaves again gets a second certificate.
    /// </summary>
    public DateTimeOffset DisconnectedUtc { get; private set; }

    public DateTimeOffset DeletedUtc { get; private set; }

    /// <summary>Total rows removed across every tenant-scoped table, audit log included.</summary>
    public int RowsDeleted { get; private set; }

    /// <summary>Per-table counts as JSON, for a specific answer rather than a total.</summary>
    public string RowCountsByTable { get; private set; } = "{}";

    /// <summary>
    /// Lowercase hex SHA-256 of the canonicalised audit log taken just before the purge
    /// (<see cref="Audit.AuditLogDigest"/>).
    /// </summary>
    public string AuditLogSha256 { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;

    private DeletionCertificate()
    {
    }

    public static DeletionCertificate Issue(
        Guid tenantId,
        string tenantDisplayName,
        DateTimeOffset disconnectedUtc,
        IReadOnlyDictionary<string, int> rowCountsByTable,
        string auditLogSha256,
        string correlationId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rowCountsByTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        if (auditLogSha256 is not { Length: 64 } || !auditLogSha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("The audit digest must be 64 lowercase hex characters.", nameof(auditLogSha256));
        }

        if (!rowCountsByTable.ContainsKey(AuditLogTableName))
        {
            // The audit count is what the digest is checked against, so it cannot be left out.
            throw new ArgumentException("The row counts must include the audit log.", nameof(rowCountsByTable));
        }

        if (rowCountsByTable.Values.Any(count => count < 0))
        {
            throw new ArgumentException("Row counts cannot be negative.", nameof(rowCountsByTable));
        }

        return new DeletionCertificate
        {
            DeletionCertificateId = Guid.NewGuid(),
            TenantId = tenantId,
            TenantDisplayName = tenantDisplayName ?? string.Empty,
            DisconnectedUtc = disconnectedUtc,
            DeletedUtc = nowUtc,
            RowsDeleted = rowCountsByTable.Values.Sum(),
            RowCountsByTable = Common.DomainJson.Serialize(
                new SortedDictionary<string, int>(rowCountsByTable.ToDictionary(), StringComparer.Ordinal)),
            AuditLogSha256 = auditLogSha256,
            CorrelationId = correlationId,
        };
    }
}
