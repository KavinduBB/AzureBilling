namespace Mlcp.Domain.Common;

/// <summary>
/// Marks an entity as belonging to exactly one customer tenant. Every business table
/// carries <see cref="TenantId"/> and it forms part of the table's unique key
/// (docs/03-architecture.md §5.1, isolation layer 1).
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
