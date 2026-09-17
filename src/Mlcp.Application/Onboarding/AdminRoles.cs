namespace Mlcp.Application.Onboarding;

/// <summary>
/// The Entra directory roles that make someone an MLCP Owner (ADR-018).
/// </summary>
/// <remarks>
/// <para>
/// Owner is derived, not granted: a person is Owner while their ID token's <c>wids</c> claim
/// holds a role that can grant tenant-wide admin consent to Microsoft Graph application
/// permissions. Those are exactly the two roles below
/// (<see href="https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent"/>).
/// </para>
/// <para>
/// The values are role <em>template</em> ids, which are the same in every tenant; <c>wids</c>
/// carries template ids, so no per-tenant lookup is needed.
/// </para>
/// </remarks>
public static class AdminRoles
{
    /// <summary>The claim type carrying directory role template ids (<c>groupMembershipClaims: DirectoryRole</c>).</summary>
    public const string DirectoryRolesClaim = "wids";

    /// <summary>Global Administrator role template id.</summary>
    public static readonly Guid GlobalAdministrator = Guid.Parse("62e90394-69f5-4237-9190-012177145e10");

    /// <summary>Privileged Role Administrator role template id.</summary>
    public static readonly Guid PrivilegedRoleAdministrator = Guid.Parse("e8611ab8-c189-46e8-94e1-60213ab1f814");

    private static readonly HashSet<Guid> ConsentAdminRoles = [GlobalAdministrator, PrivilegedRoleAdministrator];

    /// <summary>
    /// True when any of <paramref name="directoryRoleIds"/> can grant tenant-wide consent.
    /// Unparseable values are ignored rather than trusted.
    /// </summary>
    public static bool CanGrantTenantWideConsent(IEnumerable<string>? directoryRoleIds)
        => directoryRoleIds is not null
            && directoryRoleIds.Any(value => Guid.TryParse(value, out var id) && ConsentAdminRoles.Contains(id));
}

/// <summary>
/// The Entra app roles defined on the core registration (ADR-023). They arrive in the token's
/// <c>roles</c> claim and are never stored by MLCP.
/// </summary>
public static class EntraAppRoles
{
    /// <summary>The claim type carrying app role values.</summary>
    public const string RolesClaim = "roles";

    /// <summary>
    /// The Phase 5 write role. Independent of Owner (separation of duties) and assigned only by
    /// the customer's admin in Enterprise applications.
    /// </summary>
    public const string SubscriptionManager = "SubscriptionManager";

    /// <summary>True when <paramref name="roles"/> contains <see cref="SubscriptionManager"/> (exact, case-sensitive).</summary>
    /// <remarks>
    /// Case-sensitive on purpose: Entra emits the value exactly as defined on the registration,
    /// and a looser comparison would accept a differently-cased role someone else defined.
    /// </remarks>
    public static bool HasSubscriptionManager(IEnumerable<string>? roles)
        => roles is not null && roles.Any(r => string.Equals(r, SubscriptionManager, StringComparison.Ordinal));
}
