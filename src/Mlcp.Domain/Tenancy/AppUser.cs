using Mlcp.Domain.Common;

namespace Mlcp.Domain.Tenancy;

/// <summary>
/// A person who may use MLCP for one tenant. Identity comes from Entra; the role is ours.
/// Scoping to specific Azure subscriptions is optional and additive (docs/03 §4.4).
/// </summary>
public class AppUser : TenantEntity
{
    private readonly List<Guid> _scopeSubscriptionIds = [];

    public Guid AppUserId { get; private set; }

    public Guid EntraObjectId { get; private set; }

    public string Upn { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public AppRole Role { get; private set; }

    /// <summary>
    /// Phase 5 write role, held in addition to <see cref="Role"/>. Kept separate from the read
    /// role so granting write access is always a deliberate second act (ADR-010).
    /// </summary>
    public bool IsSubscriptionManager { get; private set; }

    /// <summary>Empty means every subscription in the tenant; otherwise an allow-list.</summary>
    public IReadOnlyCollection<Guid> ScopeSubscriptionIds => _scopeSubscriptionIds.AsReadOnly();

    public DateTimeOffset? LastSeenUtc { get; private set; }

    private AppUser()
    {
    }

    private AppUser(Guid tenantId, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
    }

    public static AppUser Create(
        Guid tenantId,
        Guid entraObjectId,
        string upn,
        string displayName,
        AppRole role,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upn);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (role is AppRole.Unknown or AppRole.SubscriptionManager)
        {
            throw new ArgumentException(
                "Role must be a read role: Owner, Analyst or Viewer. SubscriptionManager is granted separately.",
                nameof(role));
        }

        return new AppUser(tenantId, nowUtc)
        {
            AppUserId = Guid.NewGuid(),
            EntraObjectId = entraObjectId,
            Upn = upn,
            DisplayName = displayName,
            Role = role,
        };
    }

    public void ChangeRole(AppRole role, DateTimeOffset nowUtc)
    {
        if (role is AppRole.Unknown or AppRole.SubscriptionManager)
        {
            throw new ArgumentException(
                "Role must be a read role. Use GrantSubscriptionManager for write access.",
                nameof(role));
        }

        Role = role;

        // Losing Owner also loses the write role: it may never outlive its prerequisite.
        if (role != AppRole.Owner)
        {
            IsSubscriptionManager = false;
        }

        Touch(nowUtc);
    }

    /// <summary>
    /// Grants the Phase 5 write role. Requires the Owner read role, so a Viewer can never
    /// reach a money-affecting operation.
    /// </summary>
    public void GrantSubscriptionManager(DateTimeOffset nowUtc)
    {
        if (Role != AppRole.Owner)
        {
            throw new DomainException("Only an Owner can also hold the SubscriptionManager role.");
        }

        IsSubscriptionManager = true;
        Touch(nowUtc);
    }

    public void RevokeSubscriptionManager(DateTimeOffset nowUtc)
    {
        IsSubscriptionManager = false;
        Touch(nowUtc);
    }

    public void SetSubscriptionScope(IEnumerable<Guid> subscriptionIds, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(subscriptionIds);
        _scopeSubscriptionIds.Clear();
        _scopeSubscriptionIds.AddRange(subscriptionIds.Distinct());
        Touch(nowUtc);
    }

    public bool CanSeeSubscription(Guid azureSubscriptionId)
        => _scopeSubscriptionIds.Count == 0 || _scopeSubscriptionIds.Contains(azureSubscriptionId);

    public void RecordSeen(DateTimeOffset nowUtc)
    {
        LastSeenUtc = nowUtc;
        Touch(nowUtc);
    }
}
