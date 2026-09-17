namespace Mlcp.Persistence.Security;

/// <summary>
/// The database roles the workload identities are placed in (ADR-019, ADR-026).
/// </summary>
/// <remarks>
/// The roles are created by <see cref="DatabaseSecurityScript"/>. Deployment adds members:
/// <list type="bullet">
/// <item><c>mlcp_web_user</c> joins <see cref="Web"/>.</item>
/// <item><c>mlcp_worker_user</c> joins <see cref="Worker"/> and <see cref="System"/>.</item>
/// </list>
/// Membership is the only thing that separates the two tiers inside the database, so a
/// role's rights here are its whole blast radius.
/// </remarks>
public static class DatabaseRoles
{
    /// <summary>The web tier. Cannot change or delete audit rows, and cannot see certificates.</summary>
    public const string Web = "mlcp_web";

    /// <summary>The sync worker. The only role that may delete audit rows or write certificates.</summary>
    public const string Worker = "mlcp_worker";

    /// <summary>
    /// Holds no object permissions. The row-level security predicate checks membership of this
    /// role before it honours the <c>IsSystem</c> session flag, so a web connection that sets
    /// the flag still sees only its own tenant.
    /// </summary>
    public const string System = "mlcp_system";

    /// <summary>Every role this application creates, in creation order.</summary>
    public static IReadOnlyList<string> All { get; } = [Web, Worker, System];
}
