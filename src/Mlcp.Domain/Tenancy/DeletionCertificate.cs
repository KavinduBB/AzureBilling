namespace Mlcp.Domain.Tenancy;

/// <summary>
/// A dated record that a tenant's data was destroyed.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <em>not</em> tenant-scoped, and deliberately outside row-level security. A
/// certificate that lived in the tenant's own rows would be deleted along with them, which
/// would leave us unable to answer the one question a departed customer or a regulator actually
/// asks: can you show that you deleted it.
/// </para>
/// <para>
/// It holds no personal data and no customer content — only the tenant id, when the deletion
/// happened, and how many rows went. The tenant id is retained because it is the only thing
/// that makes the record verifiable, and by itself it identifies an organisation, not a person.
/// </para>
/// </remarks>
public class DeletionCertificate
{
    public Guid DeletionCertificateId { get; private set; }

    /// <summary>The Entra tenant whose data was destroyed.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Display name at the time of deletion, so the record is legible to a human.</summary>
    public string TenantDisplayName { get; private set; } = string.Empty;

    public DateTimeOffset DisconnectedUtc { get; private set; }

    public DateTimeOffset DeletedUtc { get; private set; }

    /// <summary>Total rows removed across every tenant-scoped table.</summary>
    public int RowsDeleted { get; private set; }

    /// <summary>Per-table counts as JSON, for a specific answer rather than a total.</summary>
    public string RowCountsByTable { get; private set; } = "{}";

    public string CorrelationId { get; private set; } = string.Empty;

    private DeletionCertificate()
    {
    }

    public static DeletionCertificate Issue(
        Guid tenantId,
        string tenantDisplayName,
        DateTimeOffset disconnectedUtc,
        IReadOnlyDictionary<string, int> rowCountsByTable,
        string correlationId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rowCountsByTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new DeletionCertificate
        {
            DeletionCertificateId = Guid.NewGuid(),
            TenantId = tenantId,
            TenantDisplayName = tenantDisplayName,
            DisconnectedUtc = disconnectedUtc,
            DeletedUtc = nowUtc,
            RowsDeleted = rowCountsByTable.Values.Sum(),
            RowCountsByTable = Common.DomainJson.Serialize(rowCountsByTable),
            CorrelationId = correlationId,
        };
    }
}
