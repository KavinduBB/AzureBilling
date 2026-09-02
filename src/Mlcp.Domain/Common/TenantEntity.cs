namespace Mlcp.Domain.Common;

/// <summary>
/// Base for every tenant-scoped entity. <see cref="TenantId"/> is assigned once at
/// construction and never changes: re-parenting a row to another tenant is not a
/// supported operation and must not be expressible in the domain model.
/// </summary>
public abstract class TenantEntity : ITenantScoped
{
    /// <summary>Owning Entra tenant. Sourced only from the validated <c>tid</c> claim.</summary>
    public Guid TenantId { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    /// <summary>Optimistic concurrency token; mapped to SQL Server <c>rowversion</c>.</summary>
    public byte[]? RowVersion { get; private set; }

    protected TenantEntity(Guid tenantId, DateTimeOffset nowUtc)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        CreatedUtc = nowUtc;
        UpdatedUtc = nowUtc;
    }

    /// <summary>Materialisation constructor for EF Core only.</summary>
    protected TenantEntity()
    {
    }

    /// <summary>Stamps <see cref="UpdatedUtc"/>. Call from every mutating method.</summary>
    protected void Touch(DateTimeOffset nowUtc) => UpdatedUtc = nowUtc;
}
