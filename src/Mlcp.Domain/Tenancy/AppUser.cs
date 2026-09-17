using Mlcp.Domain.Common;

namespace Mlcp.Domain.Tenancy;

/// <summary>
/// A person who may use MLCP for one tenant. Identity comes from Entra; the role is ours.
/// Scoping to specific Azure subscriptions is optional and additive (docs/03 §4.4).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Role"/> is recorded for display and audit. Owner is not a grant: it mirrors
/// whether the person currently holds a directory role that can grant tenant-wide consent, and
/// is re-derived at every sign-in (ADR-018). Destructive actions re-check the live claim rather
/// than trusting this column.
/// </para>
/// <para>
/// There is no write-role flag. <c>SubscriptionManager</c> is an Entra app role read from the
/// token (ADR-023).
/// </para>
/// </remarks>
public class AppUser : TenantEntity
{
    private readonly List<Guid> _scopeSubscriptionIds = [];

    public Guid AppUserId { get; private set; }

    public Guid EntraObjectId { get; private set; }

    public string Upn { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public AppRole Role { get; private set; }

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
        EnsureDefined(role);

        return new AppUser(tenantId, nowUtc)
        {
            AppUserId = Guid.NewGuid(),
            EntraObjectId = entraObjectId,
            Upn = upn,
            DisplayName = displayName,
            Role = role,
        };
    }

    /// <summary>
    /// Creates the record for someone signing in for the first time. Directory admins are Owner;
    /// everyone else is Viewer, including the very first person from the tenant (ADR-018).
    /// </summary>
    public static AppUser FirstSignIn(
        Guid tenantId,
        Guid entraObjectId,
        string upn,
        string displayName,
        bool isDirectoryAdmin,
        DateTimeOffset nowUtc)
        => Create(tenantId, entraObjectId, upn, displayName, isDirectoryAdmin ? AppRole.Owner : AppRole.Viewer, nowUtc);

    /// <summary>
    /// Sets an in-app role. Owner is accepted only because it is derived from the directory by
    /// <see cref="ApplyDirectoryAdminStatus"/>; Phase 1 role management must not offer it.
    /// </summary>
    public void ChangeRole(AppRole role, DateTimeOffset nowUtc)
    {
        EnsureDefined(role);
        Role = role;
        Touch(nowUtc);
    }

    /// <summary>
    /// Re-derives Owner from the directory at sign-in (ADR-018). An admin is upgraded to Owner; a
    /// former admin is downgraded to Viewer. Analyst and Viewer are left alone for non-admins,
    /// because those are MLCP's own assignments.
    /// </summary>
    /// <returns>The previous role when it changed; otherwise null.</returns>
    public AppRole? ApplyDirectoryAdminStatus(bool isDirectoryAdmin, DateTimeOffset nowUtc)
    {
        var previous = Role;
        var desired = isDirectoryAdmin
            ? AppRole.Owner
            : Role == AppRole.Owner ? AppRole.Viewer : Role;

        if (desired == previous)
        {
            return null;
        }

        ChangeRole(desired, nowUtc);
        return previous;
    }

    /// <summary>Keeps the displayed identity in step with the directory.</summary>
    public void UpdateProfile(string upn, string displayName, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upn);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (string.Equals(Upn, upn, StringComparison.Ordinal)
            && string.Equals(DisplayName, displayName, StringComparison.Ordinal))
        {
            return;
        }

        Upn = upn;
        DisplayName = displayName;
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

    private static void EnsureDefined(AppRole role)
    {
        if (role is not (AppRole.Owner or AppRole.Analyst or AppRole.Viewer))
        {
            throw new ArgumentException("Role must be Owner, Analyst or Viewer.", nameof(role));
        }
    }

    /// <summary>Persistence projection of <see cref="ScopeSubscriptionIds"/> as a JSON column.</summary>
    private string ScopeSubscriptionIdsJson
    {
        get => DomainJson.Serialize(_scopeSubscriptionIds);
        set
        {
            _scopeSubscriptionIds.Clear();

            if (DomainJson.Deserialize<List<Guid>>(value) is { } restored)
            {
                _scopeSubscriptionIds.AddRange(restored);
            }
        }
    }
}
