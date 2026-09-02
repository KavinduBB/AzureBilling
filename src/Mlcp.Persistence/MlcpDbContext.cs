using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Common;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Persistence;

/// <summary>
/// The application database context.
/// </summary>
/// <remarks>
/// <para>
/// Implements isolation layer 2 of the four (docs/03-architecture.md §5.1): a global query
/// filter is applied to <em>every</em> entity implementing <see cref="ITenantScoped"/>, found
/// by walking the model rather than listed by hand. A developer adding a new tenant-scoped
/// entity gets the filter automatically; forgetting is not possible, which is the only way a
/// rule like this survives contact with a growing schema.
/// </para>
/// <para>
/// Layer 3 (row-level security) is enforced by the database itself and does not depend on this
/// class being used correctly. Layer 2 exists so that queries are efficient and so that a
/// mistake shows up as zero rows rather than a SQL error.
/// </para>
/// </remarks>
public class MlcpDbContext : DbContext
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(MlcpDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{nameof(ApplyTenantFilter)} not found.");

    private readonly ITenantContext _tenantContext;

    public MlcpDbContext(DbContextOptions<MlcpDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    }

    /// <summary>
    /// The tenant every query is confined to. Read by the global query filters as a parameter,
    /// so a single compiled query plan serves every tenant.
    /// </summary>
    public Guid? CurrentTenantId => _tenantContext.TenantId;

    /// <summary>
    /// True only for the sync scheduler. Widens the query filters in the same way the
    /// row-level security predicate is widened, so the two layers agree.
    /// </summary>
    public bool IsSystemContext => _tenantContext.IsSystem;

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantCapabilityProfile> TenantCapabilityProfiles => Set<TenantCapabilityProfile>();

    public DbSet<OnboardingStep> OnboardingSteps => Set<OnboardingStep>();

    public DbSet<PendingConsentRequest> PendingConsentRequests => Set<PendingConsentRequest>();

    public DbSet<AppUser> AppUsers => Set<AppUser>();

    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MlcpDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.ClrType.IsAssignableTo(typeof(ITenantScoped)) && !entityType.IsOwned())
            {
                ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Blocks a write that would place a row in another tenant.
    /// </summary>
    /// <remarks>
    /// The query filter constrains reads but says nothing about writes: an entity constructed
    /// with the wrong tenant id would insert happily and only be caught by row-level security,
    /// as an opaque database error at the end of a unit of work. Catching it here names the
    /// entity and fails before any I/O.
    /// </remarks>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardTenantOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardTenantOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void GuardTenantOwnership()
    {
        if (IsSystemContext)
        {
            return;
        }

        var currentTenantId = CurrentTenantId;

        foreach (var entry in ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (currentTenantId is null)
            {
                throw new InvalidOperationException(
                    $"Refusing to persist {entry.Entity.GetType().Name}: no tenant is in scope.");
            }

            if (entry.Entity.TenantId != currentTenantId)
            {
                throw new InvalidOperationException(
                    $"Refusing to persist {entry.Entity.GetType().Name} owned by tenant {entry.Entity.TenantId} "
                    + $"while tenant {currentTenantId} is in scope.");
            }
        }
    }

    /// <summary>
    /// Applies the tenant predicate to one entity type.
    /// </summary>
    /// <remarks>
    /// The filter reads <see cref="CurrentTenantId"/> and <see cref="IsSystemContext"/> as
    /// instance members, which EF Core lifts into query parameters rather than baking into the
    /// cached plan. When no tenant is in scope <see cref="CurrentTenantId"/> is null and the
    /// comparison matches nothing — the safe direction to fail in.
    /// </remarks>
    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped
    {
        Expression<Func<TEntity, bool>> filter =
            entity => IsSystemContext || entity.TenantId == CurrentTenantId;

        modelBuilder.Entity<TEntity>().HasQueryFilter(filter);
    }
}
