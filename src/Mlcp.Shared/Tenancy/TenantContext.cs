namespace Mlcp.Shared.Tenancy;

/// <summary>
/// Scoped, write-once tenant context. Registered per request in the web application and per
/// dispatched job in the sync worker.
/// </summary>
/// <remarks>
/// Write-once is deliberate. If the tenant could be reassigned mid-scope, a DbContext that had
/// already opened a connection would keep the old <c>SESSION_CONTEXT</c> while the EF query
/// filter used the new value, and the two isolation layers would disagree. Making that
/// unrepresentable is cheaper than detecting it.
/// </remarks>
public sealed class TenantContext : ITenantContext
{
    private Guid? _tenantId;
    private bool _isSystem;
    private bool _assigned;

    public Guid? TenantId => _tenantId;

    public bool IsSystem => _isSystem;

    /// <summary>
    /// Binds this scope to a tenant. Called by the tenant-resolution middleware from the
    /// validated <c>tid</c> claim, or by the sync worker from the dispatched job message.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope is already bound.</exception>
    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        EnsureUnassigned();
        _tenantId = tenantId;
        _isSystem = false;
        _assigned = true;
    }

    /// <summary>
    /// Binds this scope to the sync scheduler's cross-tenant identity. Only the sync worker
    /// host may call this; the web application does not expose a path to it.
    /// </summary>
    public void SetSystem()
    {
        EnsureUnassigned();
        _tenantId = null;
        _isSystem = true;
        _assigned = true;
    }

    private void EnsureUnassigned()
    {
        if (_assigned)
        {
            throw new InvalidOperationException(
                "The tenant context is already bound for this scope and cannot be reassigned. Create a new scope instead.");
        }
    }
}

/// <summary>
/// A fixed tenant context, for tests and for background work that already knows its tenant.
/// </summary>
public sealed class FixedTenantContext(Guid? tenantId, bool isSystem = false) : ITenantContext
{
    public Guid? TenantId { get; } = tenantId;

    public bool IsSystem { get; } = isSystem;

    public static FixedTenantContext System { get; } = new(null, isSystem: true);

    public static FixedTenantContext For(Guid tenantId) => new(tenantId);
}
