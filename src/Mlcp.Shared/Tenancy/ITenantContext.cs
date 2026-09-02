namespace Mlcp.Shared.Tenancy;

/// <summary>
/// The tenant every data access in the current scope is confined to.
/// </summary>
/// <remarks>
/// <para>
/// The value comes from exactly one place: the validated <c>tid</c> claim of the signed-in
/// principal, or the tenant a sync job was dispatched for. It is never read from a route
/// value, header, query string or request body — accepting a tenant id from the caller would
/// turn all four isolation layers into a single trusted input (CLAUDE.md rule 2).
/// </para>
/// <para>
/// This is isolation layer 2's input: the EF global query filter reads
/// <see cref="TenantId"/>, and the connection interceptor writes it into
/// <c>SESSION_CONTEXT</c> for layer 3.
/// </para>
/// </remarks>
public interface ITenantContext
{
    /// <summary>The current tenant, or null when no tenant has been resolved.</summary>
    Guid? TenantId { get; }

    /// <summary>
    /// True only for the sync scheduler's cross-tenant queries. Sets <c>SESSION_CONTEXT</c>
    /// <c>IsSystem</c>, which bypasses the row-level security predicate. The web application
    /// never sets this.
    /// </summary>
    bool IsSystem { get; }

    /// <summary>True when a concrete tenant is in scope.</summary>
    bool HasTenant { get; }

    /// <summary>
    /// The current tenant, or a throw. Use wherever a missing tenant is a programming error
    /// rather than an expected state, so the failure is loud instead of a silent cross-tenant read.
    /// </summary>
    Guid RequireTenantId();
}

/// <summary>
/// Shared implementations for <see cref="ITenantContext"/>.
/// </summary>
/// <remarks>
/// Declared as real interface members with a helper here rather than as default interface
/// implementations: a default implementation is invisible through the concrete type, so
/// <c>tenantContext.RequireTenantId()</c> would fail to compile wherever the concrete class is
/// held, which is exactly where it is most useful.
/// </remarks>
internal static class TenantContextGuards
{
    internal const string NoTenantMessage =
        "No tenant is in scope. A tenant-scoped operation ran outside an authenticated request or a dispatched sync job.";

    internal static Guid Require(Guid? tenantId)
        => tenantId ?? throw new InvalidOperationException(NoTenantMessage);
}
