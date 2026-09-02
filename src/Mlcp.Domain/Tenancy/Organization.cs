namespace Mlcp.Domain.Tenancy;

/// <summary>
/// Optional roll-up above <see cref="Tenant"/> for enterprises that hold several Entra
/// tenants under one agreement. Not tenant-scoped: it is the parent of tenants, so it is
/// excluded from row-level security and guarded at the application layer instead.
/// </summary>
public class Organization
{
    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The tenant whose Owners may administer the whole organisation.</summary>
    public Guid HomeTenantId { get; private set; }

    public string Region { get; private set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; private set; }

    private Organization()
    {
    }

    public static Organization Create(string name, Guid homeTenantId, string region, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        if (homeTenantId == Guid.Empty)
        {
            throw new ArgumentException("HomeTenantId must not be empty.", nameof(homeTenantId));
        }

        return new Organization
        {
            OrganizationId = Guid.NewGuid(),
            Name = name,
            HomeTenantId = homeTenantId,
            Region = region,
            CreatedUtc = nowUtc,
        };
    }
}
